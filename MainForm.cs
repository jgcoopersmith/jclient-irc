using System.Drawing;
using System.Windows.Forms;

namespace IRCClient;

public partial class MainForm : Form
{
    private IrcConnection? _irc;
    private bool _connecting;
    private bool _explicitQuit; // user issued /quit; window close must not send another QUIT
    private bool _closing;      // form is shutting down; ignore late connection events

    // Channel tabs: channel name -> (TabPage, header label, RichTextBox, nick
    // list, body). "body" wraps the log, its splitter and the nick list so the
    // three travel together when a window is moved into a split pane.
    private readonly Dictionary<string, (TabPage tab, Label header, RichTextBox log, ListBox nicks, Panel body)> _channels = new(StringComparer.OrdinalIgnoreCase);
    private string _currentTarget = "";

    // Channel topics (from 332 on join and TOPIC changes) and the server we're
    // connected to, both shown in every window's header line.
    private readonly Dictionary<string, string> _topics = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeServer;

    // Channel modes (e.g. "+tn", plus key/limit args), from 324 replies to the
    // MODE query sent on join and re-queried after any mode change; shown in
    // the window header between server and topic.
    private readonly Dictionary<string, string> _channelModes = new(StringComparer.OrdinalIgnoreCase);

    // Who is in each channel (from NAMES on join, then joins/parts/kicks/modes),
    // mapping nick -> mode flags ("o" op, "v" voice, "ov", or ""). Used to scope
    // quit/nick messages to the right channels and to prefix speakers' nicks.
    private readonly Dictionary<string, Dictionary<string, string>> _channelUsers = new(StringComparer.OrdinalIgnoreCase);

    // Input command history, browsed with Up/Down. _historyIndex ==
    // _inputHistory.Count means "past the newest entry" (the live draft).
    private readonly List<string> _inputHistory = [];
    private int _historyIndex;
    private string _historyDraft = "";

    // Tabs that received messages while not the active tab; drawn highlighted
    // until the user opens them.
    private readonly HashSet<string> _unreadTabs = new(StringComparer.OrdinalIgnoreCase);

    // Split view: Ctrl+click marks tabs (drawn with a selection background),
    // right-click offers stacking them; while stacked, the tab strip is hidden
    // and the chosen logs are tiled in _splitPanel instead.
    private readonly HashSet<string> _ctrlSelectedTabs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _splitChannels = [];
    private readonly List<Panel> _splitPanes = [];
    private readonly Dictionary<string, Label> _splitHeaders = new(StringComparer.OrdinalIgnoreCase);
    private readonly TableLayoutPanel _splitPanel = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly ContextMenuStrip _splitMenu = new();
    private bool _splitHorizontal;
    private bool InSplitMode => _splitChannels.Count > 0;

    // Drag-to-swap state: pane header being dragged and the pane under the cursor
    private string? _dragSourceChannel;
    private string? _dropTargetChannel;

    // Open channel-settings dialog fed by 367/368 ban-list replies, and the
    // channel it's showing.
    private ChannelSettingsForm? _channelSettingsForm;
    private string? _channelSettingsChannel;

    // Which server the current channel windows belong to; connecting to a
    // different one closes them all.
    private string? _windowsServer;

    // The bold "Connections" label; kept so font re-application preserves bold
    private Label? _libraryHeader;

    // Parsed aliases: lowercased name -> command template (mIRC-style).
    private Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);

    // Rebuild the alias table from the raw multi-line setting text
    private void ParseAliases()
    {
        _aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in _settings.Aliases.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || !line.StartsWith('/')) continue;
            var sp = line.IndexOf(' ');
            if (sp < 0) continue;
            var name = line[1..sp].Trim();
            var body = line[(sp + 1)..].Trim();
            if (name.Length > 0 && body.Length > 0)
                _aliases[name] = body;
        }
    }

    // Expand a mIRC-style alias template against the given argument list.
    // Supports $N, $N-, $N-M, $$N (required), $+ (concatenation), $? (prompt).
    // Returns null if a required parameter ($$N) is missing.
    private string? ExpandAlias(string template, string[] args)
    {
        var sb = new System.Text.StringBuilder();
        int i = 0;
        while (i < template.Length)
        {
            char c = template[i];
            if (c != '$') { sb.Append(c); i++; continue; }

            // $+ : concatenation marker — drop it and the surrounding spaces
            if (i + 1 < template.Length && template[i + 1] == '+')
            {
                while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
                i += 2;
                while (i < template.Length && template[i] == ' ') i++;
                continue;
            }

            // $? : prompt the user for a value
            if (i + 1 < template.Length && template[i + 1] == '?')
            {
                var val = "";
                if (!PromptText("Alias input", "Enter value:", ref val)) return null;
                sb.Append(val);
                i += 2;
                continue;
            }

            bool required = i + 1 < template.Length && template[i + 1] == '$';
            int j = i + (required ? 2 : 1);
            int numStart = j;
            while (j < template.Length && char.IsDigit(template[j])) j++;
            if (j == numStart) { sb.Append('$'); i++; continue; } // lone $, keep literal

            int from = int.Parse(template[numStart..j]);
            int? to = from;            // default single param
            if (j < template.Length && template[j] == '-')
            {
                j++;
                int rangeStart = j;
                while (j < template.Length && char.IsDigit(template[j])) j++;
                to = j > rangeStart ? int.Parse(template[rangeStart..j]) : null; // "$N-" => open-ended
            }

            if (required && from > args.Length) return null; // $$N missing

            if (from >= 1 && from <= args.Length)
            {
                int end = to.HasValue ? Math.Min(to.Value, args.Length) : args.Length;
                if (end >= from)
                    sb.Append(string.Join(' ', args[(from - 1)..end]));
            }
            i = j;
        }
        return sb.ToString();
    }

    // Closes every window except (server) and clears all per-channel state
    private void CloseAllChannelWindows()
    {
        if (InSplitMode) ExitSplit(); // returns logs to their tabs first
        foreach (var name in _channels.Keys.Where(k => k != "(server)").ToList())
        {
            var ch = _channels[name];
            _channels.Remove(name);
            _tabs.TabPages.Remove(ch.tab);
        }
        _unreadTabs.Clear();
        _ctrlSelectedTabs.Clear();
        _topics.Clear();
        _channelUsers.Clear();
        _channelModes.Clear();
        _currentTarget = "(server)";
        if (_channels.TryGetValue("(server)", out var srv))
            _tabs.SelectedTab = srv.tab;
        _tabs.Invalidate();
        UpdateAllHeaders();
    }

    // Version as shown in About: Application.ProductVersion minus the SDK's
    // "+commithash" suffix. Used for the quit message and CTCP VERSION replies.
    private static string VersionString
    {
        get
        {
            var v = Application.ProductVersion;
            int plus = v.IndexOf('+');
            return plus >= 0 ? v[..plus] : v;
        }
    }

    // Default QUIT message: "jclient <version>"
    private static string QuitMessage => $"jclient {VersionString}";

    // Controls
    private readonly TableLayoutPanel _mainLayout = new() { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly Panel _inputPanel = new() { Dock = DockStyle.Fill };
    private readonly TextBox _inputBox = new() { Dock = DockStyle.Fill, Font = new Font("Consolas", 10) };
    private readonly Button _sendBtn = new() { Text = "Send", Dock = DockStyle.Right };
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _statusLabel = new() { Text = "Disconnected" };
    private readonly MenuStrip _menu = new();

    // Connection library controls
    private readonly Panel _libraryPanel = new() { Dock = DockStyle.Left };
    private readonly ListBox _connList = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Button _newConnBtn = new() { Text = "New" };
    private readonly Button _editConnBtn = new() { Text = "Edit", Enabled = false };
    private readonly Button _deleteConnBtn = new() { Text = "Delete", Enabled = false };
    private readonly Button _connectSavedBtn = new() { Text = "Connect", Enabled = false };
    private readonly Button _disconnectBtn = new() { Text = "Disconnect", Enabled = false };
    private List<SavedConnection> _savedConnections = [];
    private List<string> _pendingAutoJoinChannels = [];
    private readonly AppSettings _settings = SettingsStore.Load();

    public MainForm()
    {
        // Legacy AutoScaleMode/AutoScaleDimensions self-calibrate against
        // whatever font metrics are current at the moment they're set, which
        // for a hand-built (non-Designer) form always equals the live DPI's
        // own metrics — giving a permanent 1.0 scale factor and leaving every
        // hardcoded pixel size unscaled at high DPI, even though GDI still
        // renders text natively larger. Verified via ContainerControl probing
        // rather than assumed. Instead, every hardcoded size below is passed
        // through LogicalToDeviceUnits, which deterministically multiplies by
        // DeviceDpi/96 — the documented API for manual DPI-aware layout.
        AutoScaleMode = AutoScaleMode.None;
        Text = "jclient irc for Windows";
        Font = new Font("Segoe UI", 9);
        Size = LogicalToDeviceUnits(new Size(900, 650));
        MinimumSize = LogicalToDeviceUnits(new Size(600, 400));
        Icon = AppIcon.Get();

        _inputPanel.Height = LogicalToDeviceUnits(36);
        _sendBtn.Width = LogicalToDeviceUnits(70);
        _libraryPanel.Width = LogicalToDeviceUnits(220);
        _libraryPanel.Padding = new Padding(LogicalToDeviceUnits(6));

        BuildLibraryPanel();

        _mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, LogicalToDeviceUnits(36)));
        // A TableLayoutPanel cell holds one control, so a host panel carries both
        // the tab view and the (initially hidden) split view.
        var viewHost = new Panel { Dock = DockStyle.Fill };
        viewHost.Controls.Add(_splitPanel);
        viewHost.Controls.Add(_tabs);
        _mainLayout.Controls.Add(viewHost, 0, 0);
        _mainLayout.Controls.Add(_inputPanel, 0, 1);

        _inputPanel.Controls.Add(_inputBox);
        _inputPanel.Controls.Add(_sendBtn);

        _status.Items.Add(_statusLabel);

        // File menu
        var fileMenu = new ToolStripMenuItem("File");
        var disconnectItem = new ToolStripMenuItem("Disconnect");
        disconnectItem.Click += OnDisconnect;
        var optionsItem = new ToolStripMenuItem("Options");
        var connectOptions = new ToolStripMenuItem("Connect");
        var connectOnStartupItem = new ToolStripMenuItem("Connect on startup")
        {
            CheckOnClick = true,
            Checked = _settings.ConnectOnStartup
        };
        connectOnStartupItem.CheckedChanged += (s, e) =>
        {
            _settings.ConnectOnStartup = connectOnStartupItem.Checked;
            SettingsStore.Save(_settings);
        };
        var reconnectOnDisconnectItem = new ToolStripMenuItem("Reconnect on disconnect")
        {
            CheckOnClick = true,
            Checked = _settings.ReconnectOnDisconnect
        };
        reconnectOnDisconnectItem.CheckedChanged += (s, e) =>
        {
            _settings.ReconnectOnDisconnect = reconnectOnDisconnectItem.Checked;
            SettingsStore.Save(_settings);
        };
        string VersionReplyText() => string.IsNullOrEmpty(_settings.CustomVersionReply)
            ? "Custom VERSION reply: (default)"
            : $"Custom VERSION reply: {_settings.CustomVersionReply}";
        var versionReplyItem = new ToolStripMenuItem(VersionReplyText());
        versionReplyItem.Click += (s, e) =>
        {
            var current = _settings.CustomVersionReply;
            if (PromptText("Custom CTCP VERSION reply", "Reply sent to VERSION requests (leave blank to use the default):", ref current))
            {
                _settings.CustomVersionReply = current.Trim();
                SettingsStore.Save(_settings);
                versionReplyItem.Text = VersionReplyText();
            }
        };
        connectOptions.DropDownItems.Add(connectOnStartupItem);
        connectOptions.DropDownItems.Add(reconnectOnDisconnectItem);
        connectOptions.DropDownItems.Add(new ToolStripSeparator());
        connectOptions.DropDownItems.Add(versionReplyItem);
        optionsItem.DropDownItems.Add(connectOptions);
        var logOptions = new ToolStripMenuItem("Log");
        string LogToggleText() => !_settings.LoggingEnabled
            ? "Logging: off"
            : string.IsNullOrEmpty(_settings.LogDirectory)
                ? "Logging: on (no folder set)"
                : $"Logging: on — {_settings.LogDirectory}";
        var logToggleItem = new ToolStripMenuItem(LogToggleText())
        {
            CheckOnClick = true,
            Checked = _settings.LoggingEnabled
        };
        bool BrowseForLogDir()
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Choose the catch-all folder where all chat and server logs are written",
                UseDescriptionForTitle = true,
                InitialDirectory = _settings.LogDirectory
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return false;
            _settings.LogDirectory = dlg.SelectedPath;
            return true;
        }
        var setLogDirItem = new ToolStripMenuItem("Set Log Directory...");
        setLogDirItem.Click += (s, e) =>
        {
            if (!BrowseForLogDir()) return;
            SettingsStore.Save(_settings);
            logToggleItem.Text = LogToggleText();
        };
        logToggleItem.CheckedChanged += (s, e) =>
        {
            // Switching on without a folder configured prompts for one; cancelling
            // the browse leaves the toggle off.
            if (logToggleItem.Checked && string.IsNullOrEmpty(_settings.LogDirectory) && !BrowseForLogDir())
            {
                logToggleItem.Checked = false; // re-enters this handler on the off path
                return;
            }
            _settings.LoggingEnabled = logToggleItem.Checked;
            SettingsStore.Save(_settings);
            logToggleItem.Text = LogToggleText();
        };
        logOptions.DropDownItems.Add(setLogDirItem);
        logOptions.DropDownItems.Add(new ToolStripSeparator());
        logOptions.DropDownItems.Add(logToggleItem);
        optionsItem.DropDownItems.Add(logOptions);
        var aboutItem = new ToolStripMenuItem("About");
        // Disabled info items carry gray text by default; ForeColor forces black
        // so the bold shows clearly. VersionString strips the SDK's +commit suffix.
        var boldFont = new Font(_menu.Font, FontStyle.Bold);
        aboutItem.DropDownItems.Add(new ToolStripMenuItem("jclient irc for Windows by j0ker") { Enabled = false, Font = boldFont, ForeColor = Color.Black });
        aboutItem.DropDownItems.Add(new ToolStripMenuItem($"version {VersionString}") { Enabled = false, Font = boldFont, ForeColor = Color.Black });
        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (s, e) => Close();
        fileMenu.DropDownItems.Add(disconnectItem);
        fileMenu.DropDownItems.Add(optionsItem);
        fileMenu.DropDownItems.Add(aboutItem);
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(exitItem);
        _menu.Items.Add(fileMenu);

        // View menu
        var viewMenu = new ToolStripMenuItem("View");
        var fullScreenItem = new ToolStripMenuItem("Full screen") { CheckOnClick = true, ShortcutKeys = Keys.F11 };
        fullScreenItem.CheckedChanged += (s, e) => SetFullScreen(fullScreenItem.Checked);
        var keepOnTopItem = new ToolStripMenuItem("Keep on top") { CheckOnClick = true, Checked = _settings.KeepOnTop };
        keepOnTopItem.CheckedChanged += (s, e) =>
        {
            TopMost = keepOnTopItem.Checked;
            _settings.KeepOnTop = keepOnTopItem.Checked;
            SettingsStore.Save(_settings);
        };
        var fontMenu = new ToolStripMenuItem("Font");
        var pickFontItem = new ToolStripMenuItem("Choose Font...");
        var pickChannelFontItem = new ToolStripMenuItem("Choose Channel Font...");
        var channelFontItem = new ToolStripMenuItem("Default channel font (chat windows only)") { CheckOnClick = true, Checked = _settings.ChannelFontEnabled };
        var defaultFontItem = new ToolStripMenuItem("Default font (enforce app-wide)") { CheckOnClick = true, Checked = _settings.DefaultFontEnabled };
        pickFontItem.Click += (s, e) =>
        {
            using var fd = new FontDialog { Font = CurrentDefaultFont() ?? Font, ShowEffects = false };
            if (fd.ShowDialog(this) != DialogResult.OK) return;
            _settings.DefaultFontFamily = fd.Font.FontFamily.Name;
            _settings.DefaultFontSize = fd.Font.Size;
            _settings.DefaultFontStyle = (int)fd.Font.Style;
            SettingsStore.Save(_settings);
            if (_settings.DefaultFontEnabled) ApplyFonts();
        };
        pickChannelFontItem.Click += (s, e) =>
        {
            using var fd = new FontDialog { Font = CurrentChannelFont() ?? new Font("Consolas", 9.5f), ShowEffects = false };
            if (fd.ShowDialog(this) != DialogResult.OK) return;
            _settings.ChannelFontFamily = fd.Font.FontFamily.Name;
            _settings.ChannelFontSize = fd.Font.Size;
            _settings.ChannelFontStyle = (int)fd.Font.Style;
            SettingsStore.Save(_settings);
            if (_settings.ChannelFontEnabled) ApplyFonts();
        };
        channelFontItem.CheckedChanged += (s, e) =>
        {
            _settings.ChannelFontEnabled = channelFontItem.Checked;
            SettingsStore.Save(_settings);
            ApplyFonts();
        };
        defaultFontItem.CheckedChanged += (s, e) =>
        {
            _settings.DefaultFontEnabled = defaultFontItem.Checked;
            SettingsStore.Save(_settings);
            ApplyFonts();
        };
        fontMenu.DropDownItems.Add(pickFontItem);
        fontMenu.DropDownItems.Add(pickChannelFontItem);
        fontMenu.DropDownItems.Add(new ToolStripSeparator());
        fontMenu.DropDownItems.Add(channelFontItem);
        fontMenu.DropDownItems.Add(defaultFontItem);
        viewMenu.DropDownItems.Add(fullScreenItem);
        viewMenu.DropDownItems.Add(keepOnTopItem);
        viewMenu.DropDownItems.Add(new ToolStripSeparator());
        viewMenu.DropDownItems.Add(fontMenu);
        _menu.Items.Add(viewMenu);

        // Tools menu
        var toolsMenu = new ToolStripMenuItem("Tools");
        var aliasItem = new ToolStripMenuItem("Alias...");
        aliasItem.Click += (s, e) =>
        {
            using var dlg = new AliasEditForm(_settings.Aliases);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _settings.Aliases = dlg.Aliases;
            SettingsStore.Save(_settings);
            ParseAliases();
        };
        toolsMenu.DropDownItems.Add(aliasItem);
        _menu.Items.Add(toolsMenu);

        MainMenuStrip = _menu;

        // Add order matters for docking: controls are docked in reverse of Controls.Add
        // order, so _mainLayout (Fill) is added first to claim whatever space is left
        // after the menu (Top), _libraryPanel (Left), and the status bar have claimed
        // theirs. The menu is added last so it docks first and spans the full width.
        Controls.Add(_mainLayout);
        Controls.Add(_libraryPanel);
        Controls.Add(_status);
        Controls.Add(_menu);

        // Server log tab
        AddChannelTab("(server)");
        _currentTarget = "(server)";

        _sendBtn.Click += OnSend;
        _inputBox.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter) { OnSend(s, e); e.SuppressKeyPress = true; }
        };

        _tabs.Selected += (s, e) =>
        {
            if (_tabs.SelectedTab != null)
            {
                _currentTarget = _tabs.SelectedTab.Text;
                if (_unreadTabs.Remove(_currentTarget))
                    _tabs.Invalidate();
            }
            // Switching windows leaves focus on the tab strip; put the caret
            // back where the user actually types. Deferred, because the tab
            // control claims focus itself after this event returns.
            BeginInvoke(() => _inputBox.Focus());
        };

        // Clicking the already-active tab raises no Selected event, so the tab
        // strip would keep focus. Nothing here is keyboard-navigable, so bounce
        // focus back to the input line whenever the strip receives it.
        _tabs.GotFocus += (s, e) => BeginInvoke(() => _inputBox.Focus());

        // Owner-draw the tab headers so tabs with unread activity and tabs
        // Ctrl+selected for stacking can be highlighted; the default renderer
        // has no per-tab colors.
        _tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        _tabs.DrawItem += (s, e) =>
        {
            if (e.Index < 0 || e.Index >= _tabs.TabCount) return;
            var tab = _tabs.TabPages[e.Index];
            if (_ctrlSelectedTabs.Contains(tab.Text))
            {
                using var sel = new SolidBrush(Color.FromArgb(176, 205, 235));
                e.Graphics.FillRectangle(sel, e.Bounds);
            }
            else
            {
                e.DrawBackground();
            }
            var color = _unreadTabs.Contains(tab.Text) ? Color.DarkOrange : _tabs.ForeColor;
            TextRenderer.DrawText(e.Graphics, tab.Text, _tabs.Font, e.Bounds, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };

        // Middle-click closes a tab; Ctrl+left-click marks tabs for stacking
        _tabs.MouseDown += (s, e) =>
        {
            if (e.Button == MouseButtons.Middle)
            {
                CloseTabAt(e.Location);
            }
            else if (e.Button == MouseButtons.Left && ModifierKeys.HasFlag(Keys.Control))
            {
                var tab = TabPageAt(e.Location);
                if (tab != null)
                {
                    if (!_ctrlSelectedTabs.Remove(tab.Text))
                        _ctrlSelectedTabs.Add(tab.Text);
                    _tabs.Invalidate();
                }
            }
        };

        // Right-click on a tab: stack options plus Close for the clicked tab
        TabPage? _rightClickedTab = null;
        var tabMenu = new ContextMenuStrip();
        var stackHItem = new ToolStripMenuItem("Stack Horizontal");
        stackHItem.Click += (s, e) => EnterSplit(horizontal: true);
        var stackVItem = new ToolStripMenuItem("Stack Vertical");
        stackVItem.Click += (s, e) => EnterSplit(horizontal: false);
        var channelSettingsItem = new ToolStripMenuItem("Channel Settings...");
        channelSettingsItem.Click += (s, e) =>
        {
            if (_rightClickedTab != null)
                OpenChannelSettings(_rightClickedTab.Text);
        };
        var logWindowItem = new ToolStripMenuItem("Stop Logging");
        logWindowItem.Click += (s, e) =>
        {
            if (_rightClickedTab == null) return;
            var name = _rightClickedTab.Text;
            if (IsWindowLoggingStopped(name))
                _settings.LoggingDisabledWindows.RemoveAll(w => w.Equals(name, StringComparison.OrdinalIgnoreCase));
            else
                _settings.LoggingDisabledWindows.Add(name);
            SettingsStore.Save(_settings);
        };
        var closeItem = new ToolStripMenuItem("Close");
        closeItem.Click += async (s, e) =>
        {
            if (_rightClickedTab != null)
                await CloseTab(_rightClickedTab.Text);
        };
        tabMenu.Items.Add(stackHItem);
        tabMenu.Items.Add(stackVItem);
        tabMenu.Items.Add(new ToolStripSeparator());
        tabMenu.Items.Add(channelSettingsItem);
        tabMenu.Items.Add(logWindowItem);
        tabMenu.Items.Add(new ToolStripSeparator());
        tabMenu.Items.Add(closeItem);
        _tabs.MouseDown += (s, e) =>
        {
            if (e.Button == MouseButtons.Right)
            {
                var tab = TabPageAt(e.Location);
                if (tab != null)
                {
                    _rightClickedTab = tab;
                    closeItem.Enabled = tab.Text != "(server)";
                    bool isChannel = tab.Text.StartsWith('#') || tab.Text.StartsWith('&');
                    channelSettingsItem.Visible = isChannel;
                    channelSettingsItem.Enabled = isChannel && (_irc?.IsConnected ?? false);
                    logWindowItem.Text = IsWindowLoggingStopped(tab.Text) ? "Start Logging" : "Stop Logging";
                    tabMenu.Show(_tabs, e.Location);
                }
            }
        };

        // Right-click menu while stacked: re-orient or go back to tabs
        _splitMenu.Items.Add("Stack Horizontal", null, (s, e) => BuildSplit([.. _splitChannels], horizontal: true));
        _splitMenu.Items.Add("Stack Vertical", null, (s, e) => BuildSplit([.. _splitChannels], horizontal: false));
        _splitMenu.Items.Add(new ToolStripSeparator());
        _splitMenu.Items.Add("Unstack", null, (s, e) => ExitSplit());

        // Ctrl+A selects all text in the input box
        _inputBox.KeyDown += (s, e) =>
        {
            if (e.Control && e.KeyCode == Keys.A)
            {
                _inputBox.SelectAll();
                e.SuppressKeyPress = true;
            }
        };

        // Up/Down browse the command history; the in-progress draft is stashed
        // on the way up and restored when arrowing back past the newest entry.
        _inputBox.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Up)
            {
                e.SuppressKeyPress = true;
                if (_inputHistory.Count == 0 || _historyIndex == 0) return;
                if (_historyIndex == _inputHistory.Count)
                    _historyDraft = _inputBox.Text;
                _historyIndex--;
                _inputBox.Text = _inputHistory[_historyIndex];
                _inputBox.SelectionStart = _inputBox.TextLength;
            }
            else if (e.KeyCode == Keys.Down)
            {
                e.SuppressKeyPress = true;
                if (_historyIndex >= _inputHistory.Count) return;
                _historyIndex++;
                _inputBox.Text = _historyIndex == _inputHistory.Count ? _historyDraft : _inputHistory[_historyIndex];
                _inputBox.SelectionStart = _inputBox.TextLength;
            }
        };

        // Right-click context menu on input box (cut/copy/paste/select all)
        var inputMenu = new ContextMenuStrip();
        inputMenu.Items.Add("Cut",    null, (s, e) => _inputBox.Cut());
        inputMenu.Items.Add("Copy",   null, (s, e) => _inputBox.Copy());
        inputMenu.Items.Add("Paste",  null, (s, e) => _inputBox.Paste());
        inputMenu.Items.Add(new ToolStripSeparator());
        inputMenu.Items.Add("Select All", null, (s, e) => _inputBox.SelectAll());
        inputMenu.Opening += (s, e) =>
        {
            inputMenu.Items[0].Enabled = _inputBox.SelectionLength > 0;
            inputMenu.Items[1].Enabled = _inputBox.SelectionLength > 0;
            inputMenu.Items[2].Enabled = Clipboard.ContainsText();
        };
        _inputBox.ContextMenuStrip = inputMenu;

        // Apply persisted View settings
        TopMost = _settings.KeepOnTop;
        if (_settings.DefaultFontEnabled || _settings.ChannelFontEnabled) ApplyFonts();
        ParseAliases();
    }

    private TabPage? TabPageAt(Point p)
    {
        for (int i = 0; i < _tabs.TabCount; i++)
            if (_tabs.GetTabRect(i).Contains(p))
                return _tabs.TabPages[i];
        return null;
    }

    private async void CloseTabAt(Point p)
    {
        var tab = TabPageAt(p);
        if (tab == null || tab.Text == "(server)") return;
        await CloseTab(tab.Text);
    }

    private async Task CloseTab(string name)
    {
        if (!_channels.TryGetValue(name, out var ch)) return;

        // Send PART for channels we're in
        if (_irc != null && _irc.IsConnected && (name.StartsWith('#') || name.StartsWith('&')))
            await _irc.PartAsync(name);

        _channels.Remove(name);
        _unreadTabs.Remove(name);
        _ctrlSelectedTabs.Remove(name);
        _topics.Remove(name);
        _channelUsers.Remove(name);
        _channelModes.Remove(name);
        _tabs.TabPages.Remove(ch.tab);

        if (_currentTarget.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            _currentTarget = "(server)";
            if (_channels.TryGetValue("(server)", out var srv))
                _tabs.SelectedTab = srv.tab;
        }

        HandleTabRemovedFromSplit(name);
    }

    // Stack the Ctrl+selected tabs, or every tab if fewer than two are selected
    private void EnterSplit(bool horizontal)
    {
        var all = _tabs.TabPages.Cast<TabPage>().Select(t => t.Text);
        var targets = _ctrlSelectedTabs.Count >= 2
            ? all.Where(_ctrlSelectedTabs.Contains).ToList()
            : all.ToList();
        _ctrlSelectedTabs.Clear();
        BuildSplit(targets, horizontal);
    }

    private void BuildSplit(List<string> channels, bool horizontal)
    {
        TearDownSplitPanes();
        _splitChannels.Clear();
        _splitChannels.AddRange(channels.Where(_channels.ContainsKey));
        if (_splitChannels.Count == 0)
        {
            ExitSplit();
            return;
        }
        _splitHorizontal = horizontal;

        int n = _splitChannels.Count;
        _splitPanel.SuspendLayout();
        _splitPanel.ColumnStyles.Clear();
        _splitPanel.RowStyles.Clear();
        _splitPanel.ColumnCount = horizontal ? n : 1;
        _splitPanel.RowCount = horizontal ? 1 : n;
        for (int i = 0; i < n; i++)
        {
            if (horizontal) _splitPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / n));
            else _splitPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / n));
        }

        for (int i = 0; i < n; i++)
        {
            var name = _splitChannels[i];
            var log = _channels[name].log;
            var header = new Label
            {
                Text = ComposeHeader(name),
                Dock = DockStyle.Top,
                Height = LogicalToDeviceUnits(20),
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.FromArgb(45, 45, 60),
                ForeColor = Color.LightGray,
                Padding = new Padding(LogicalToDeviceUnits(4), 0, 0, 0),
                AutoEllipsis = true,
                ContextMenuStrip = _splitMenu
            };
            var pane = new Panel { Dock = DockStyle.Fill, Margin = new Padding(LogicalToDeviceUnits(1)), ContextMenuStrip = _splitMenu };
            // Move the whole body so the nick list follows its log into the pane
            var body = _channels[name].body;
            body.Parent?.Controls.Remove(body);
            log.ContextMenuStrip = _splitMenu;
            pane.Controls.Add(body);
            pane.Controls.Add(header);
            _splitPanes.Add(pane);
            _splitHeaders[name] = header;
            _splitPanel.Controls.Add(pane, horizontal ? i : 0, horizontal ? 0 : i);

            // Drag a pane by its header onto another pane to swap positions
            var dragName = name;
            header.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                _dragSourceChannel = dragName;
                header.Cursor = Cursors.SizeAll;
            };
            header.MouseMove += (s, e) =>
            {
                if (_dragSourceChannel == null) return;
                var over = PaneChannelAt(MousePosition);
                var target = over != null && !over.Equals(_dragSourceChannel, StringComparison.OrdinalIgnoreCase) ? over : null;
                if (!string.Equals(target, _dropTargetChannel, StringComparison.OrdinalIgnoreCase))
                {
                    _dropTargetChannel = target;
                    UpdateSplitHeaderColors();
                }
            };
            header.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Left || _dragSourceChannel == null) return;
                var src = _dragSourceChannel;
                var dst = PaneChannelAt(MousePosition);
                _dragSourceChannel = null;
                _dropTargetChannel = null;
                header.Cursor = Cursors.Default;
                UpdateSplitHeaderColors();
                // Defer the swap: rebuilding disposes this header while its own
                // MouseUp handler is still on the stack.
                if (dst != null && !dst.Equals(src, StringComparison.OrdinalIgnoreCase))
                    BeginInvoke(() => SwapSplitPanes(src, dst));
            };
        }
        _splitPanel.ResumeLayout();

        _tabs.Visible = false;
        _splitPanel.Visible = true;
        if (!_splitChannels.Contains(_currentTarget, StringComparer.OrdinalIgnoreCase))
            _currentTarget = _splitChannels[0];
        UpdateSplitHeaderColors();
    }

    // Returns every stacked log to its own TabPage and disposes the panes
    private void TearDownSplitPanes()
    {
        foreach (var name in _splitChannels)
        {
            if (!_channels.TryGetValue(name, out var ch)) continue;
            ch.body.Parent?.Controls.Remove(ch.body);
            ch.log.ContextMenuStrip = null;
            ch.tab.Controls.Add(ch.body);
            // The header must dock before the body claims the remaining space,
            // and docking runs from the highest child index down.
            ch.tab.Controls.SetChildIndex(ch.body, 0);
        }
        _splitPanel.Controls.Clear();
        foreach (var pane in _splitPanes)
            pane.Dispose();
        _splitPanes.Clear();
        _splitHeaders.Clear();
    }

    private void ExitSplit()
    {
        TearDownSplitPanes();
        // Everything in the split was visible, so nothing in it is unread
        foreach (var name in _splitChannels)
            _unreadTabs.Remove(name);
        _splitChannels.Clear();
        _splitPanel.Visible = false;
        _tabs.Visible = true;
        _currentTarget = _tabs.SelectedTab?.Text ?? "(server)";
        _tabs.Invalidate();
    }

    // Mark the pane whose messages the input box sends to
    private void SetSplitCurrentTarget(string name)
    {
        _currentTarget = name;
        UpdateSplitHeaderColors();
    }

    private void UpdateSplitHeaderColors()
    {
        foreach (var (name, header) in _splitHeaders)
        {
            bool dropTarget = name.Equals(_dropTargetChannel, StringComparison.OrdinalIgnoreCase);
            bool active = name.Equals(_currentTarget, StringComparison.OrdinalIgnoreCase);
            header.BackColor = dropTarget ? Color.FromArgb(180, 120, 40)
                             : active ? Color.FromArgb(60, 90, 150)
                             : Color.FromArgb(45, 45, 60);
            header.ForeColor = active || dropTarget ? Color.White : Color.LightGray;
        }
    }

    // Which stacked pane (by channel name) is under the given screen point
    private string? PaneChannelAt(Point screenPoint)
    {
        for (int i = 0; i < _splitPanes.Count && i < _splitChannels.Count; i++)
        {
            var pane = _splitPanes[i];
            if (!pane.IsDisposed && pane.RectangleToScreen(pane.ClientRectangle).Contains(screenPoint))
                return _splitChannels[i];
        }
        return null;
    }

    private void SwapSplitPanes(string from, string to)
    {
        int i = _splitChannels.FindIndex(c => c.Equals(from, StringComparison.OrdinalIgnoreCase));
        int j = _splitChannels.FindIndex(c => c.Equals(to, StringComparison.OrdinalIgnoreCase));
        if (i < 0 || j < 0 || i == j) return;
        (_splitChannels[i], _splitChannels[j]) = (_splitChannels[j], _splitChannels[i]);
        BuildSplit([.. _splitChannels], _splitHorizontal);
    }

    // Rebuilds (or exits) the split when one of its channels closes
    private void HandleTabRemovedFromSplit(string name)
    {
        int idx = _splitChannels.FindIndex(c => c.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return;
        _splitChannels.RemoveAt(idx);
        if (_splitChannels.Count == 0)
            ExitSplit();
        else
            BuildSplit([.. _splitChannels], _splitHorizontal);
    }

    private FormBorderStyle _preFullScreenBorder;
    private FormWindowState _preFullScreenState;

    private void SetFullScreen(bool on)
    {
        if (on)
        {
            _preFullScreenBorder = FormBorderStyle;
            _preFullScreenState = WindowState;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal; // must leave Maximized before restyling
            WindowState = FormWindowState.Maximized;
        }
        else
        {
            FormBorderStyle = _preFullScreenBorder;
            WindowState = _preFullScreenState;
        }
    }

    // The user's configured default font, or null if none has been chosen
    private Font? CurrentDefaultFont() =>
        string.IsNullOrEmpty(_settings.DefaultFontFamily)
            ? null
            : new Font(_settings.DefaultFontFamily, _settings.DefaultFontSize, (FontStyle)_settings.DefaultFontStyle);

    // The user's configured channel font, or null if none has been chosen
    private Font? CurrentChannelFont() =>
        string.IsNullOrEmpty(_settings.ChannelFontFamily)
            ? null
            : new Font(_settings.ChannelFontFamily, _settings.ChannelFontSize, (FontStyle)_settings.ChannelFontStyle);

    // Font for a message log: channel font wins in chat channel windows, then
    // the app-wide default font, then the built-in Consolas.
    private Font LogFontFor(string name)
    {
        bool isChannel = name.StartsWith('#') || name.StartsWith('&');
        if (isChannel && _settings.ChannelFontEnabled && CurrentChannelFont() is { } cf) return cf;
        if (_settings.DefaultFontEnabled && CurrentDefaultFont() is { } df) return df;
        return new Font("Consolas", 9.5f);
    }

    // Applies the font settings across the app: the app-wide default (or the
    // baseline Segoe UI when off) everywhere, then per-window log fonts.
    private void ApplyFonts()
    {
        var effective = (_settings.DefaultFontEnabled ? CurrentDefaultFont() : null) ?? new Font("Segoe UI", 9f);
        Font = effective;
        void Recurse(Control.ControlCollection controls)
        {
            foreach (Control c in controls)
            {
                c.Font = effective;
                Recurse(c.Controls);
            }
        }
        Recurse(Controls);
        _menu.Font = effective;
        // Restore the specials the blanket pass flattened
        _inputBox.Font = _settings.DefaultFontEnabled && CurrentDefaultFont() is { } d ? d : new Font("Consolas", 10);
        if (_libraryHeader != null) _libraryHeader.Font = new Font(effective, FontStyle.Bold);
        foreach (var (name, ch) in _channels)
        {
            ch.log.Font = LogFontFor(name);
            ch.nicks.Font = LogFontFor(name); // the nick list tracks its own log
        }
        UpdateAllHeaders();
    }

    // Minimal single-line text prompt (WinForms has no built-in InputBox).
    private bool PromptText(string title, string prompt, ref string value)
    {
        using var dlg = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent,
            Font = new Font("Segoe UI", 9),
            Icon = AppIcon.Get(),
            ClientSize = LogicalToDeviceUnits(new Size(440, 120))
        };
        int L(int v) => LogicalToDeviceUnits(v);
        var label = new Label { Text = prompt, Location = new Point(L(12), L(12)), Size = new Size(L(416), L(34)), AutoSize = false };
        var box = new TextBox { Location = new Point(L(12), L(50)), Size = new Size(L(416), L(24)), Text = value };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Size = LogicalToDeviceUnits(new Size(90, 28)), Location = new Point(L(228), L(84)) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = LogicalToDeviceUnits(new Size(90, 28)), Location = new Point(L(338), L(84)) };
        dlg.Controls.AddRange([label, box, ok, cancel]);
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        if (dlg.ShowDialog(this) != DialogResult.OK) return false;
        value = box.Text;
        return true;
    }

    private void OpenChannelSettings(string channel)
    {
        if (_irc is not { IsConnected: true }) return;
        _channelModes.TryGetValue(channel, out var modes);
        _topics.TryGetValue(channel, out var topic);

        using var dlg = new ChannelSettingsForm(channel, topic ?? "", modes ?? "",
            line => _ = _irc?.SendRawAsync(line));
        _channelSettingsForm = dlg;
        _channelSettingsChannel = channel;
        // Request the ban list; 367/368 replies arrive during ShowDialog's modal
        // loop (posted callbacks still pump) and are forwarded to the dialog.
        _ = _irc.SendRawAsync($"MODE {channel} +b");
        try { dlg.ShowDialog(this); }
        finally { _channelSettingsForm = null; _channelSettingsChannel = null; }
    }

    private void BuildLibraryPanel()
    {
        var header = new Label
        {
            Text = "Connections",
            Dock = DockStyle.Top,
            Height = LogicalToDeviceUnits(22),
            Font = new Font(Font, FontStyle.Bold)
        };
        _libraryHeader = header;

        var btnLayout = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = LogicalToDeviceUnits(94), ColumnCount = 2, RowCount = 3 };
        btnLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        btnLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        // RowCount alone doesn't give every row an equal share of the panel's
        // height — without explicit RowStyles, unstyled rows size to content and
        // whichever row is last soaks up the rest, making Disconnect huge relative
        // to New/Edit/Delete/Connect (most visible at high DPI where the panel's
        // total height is much larger than the buttons' natural content size).
        for (int i = 0; i < 3; i++)
            btnLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 3));
        foreach (var b in new[] { _newConnBtn, _editConnBtn, _deleteConnBtn, _connectSavedBtn, _disconnectBtn })
        {
            b.Dock = DockStyle.Fill;
            b.Margin = new Padding(LogicalToDeviceUnits(2));
        }
        btnLayout.Controls.Add(_newConnBtn, 0, 0);
        btnLayout.Controls.Add(_editConnBtn, 1, 0);
        btnLayout.Controls.Add(_deleteConnBtn, 0, 1);
        btnLayout.Controls.Add(_connectSavedBtn, 1, 1);
        btnLayout.Controls.Add(_disconnectBtn, 0, 2);
        btnLayout.SetColumnSpan(_disconnectBtn, 2);

        _libraryPanel.Controls.Add(_connList);
        _libraryPanel.Controls.Add(btnLayout);
        _libraryPanel.Controls.Add(header);

        _savedConnections = ConnectionStore.Load();
        RefreshConnList();

        _connList.SelectedIndexChanged += (s, e) =>
        {
            bool has = _connList.SelectedIndex >= 0;
            _editConnBtn.Enabled = has;
            _deleteConnBtn.Enabled = has;
            _connectSavedBtn.Enabled = has;
        };

        _connList.DoubleClick += (s, e) => ConnectToSelected();
        _connectSavedBtn.Click += (s, e) => ConnectToSelected();
        _disconnectBtn.Click += OnDisconnect;

        _newConnBtn.Click += (s, e) =>
        {
            using var dlg = new ConnectionEditForm(null);
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _savedConnections.Add(dlg.Result);
                WarnIfSaveFailed(ConnectionStore.Save(_savedConnections));
                RefreshConnList(_savedConnections.Count - 1);
            }
        };

        _editConnBtn.Click += (s, e) => EditSelected();

        _deleteConnBtn.Click += (s, e) =>
        {
            int idx = _connList.SelectedIndex;
            if (idx < 0) return;
            var name = _savedConnections[idx].Name;
            var confirm = MessageBox.Show(this, $"Delete connection \"{name}\"?", "Confirm Delete",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            _savedConnections.RemoveAt(idx);
            WarnIfSaveFailed(ConnectionStore.Save(_savedConnections));
            RefreshConnList();
        };
    }

    private void EditSelected()
    {
        int idx = _connList.SelectedIndex;
        if (idx < 0) return;
        using var dlg = new ConnectionEditForm(_savedConnections[idx]);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _savedConnections[idx] = dlg.Result;
            WarnIfSaveFailed(ConnectionStore.Save(_savedConnections));
            RefreshConnList(idx);
        }
    }

    private void WarnIfSaveFailed(bool saveSucceeded)
    {
        if (saveSucceeded) return;
        MessageBox.Show(this, "Could not save the connection library to disk.", "Save Failed",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // selectIndex defaults to -1 (no selection) rather than trying to preserve
    // whatever was selected before the list was mutated: after a delete, the old
    // index may now point at a different item, so silently reselecting it would
    // enable Edit/Delete/Connect against a connection the user didn't click.
    private void RefreshConnList(int selectIndex = -1)
    {
        _connList.Items.Clear();
        foreach (var c in _savedConnections)
            _connList.Items.Add(c.Name);
        if (selectIndex >= 0 && selectIndex < _connList.Items.Count)
            _connList.SelectedIndex = selectIndex;
    }

    private async void ConnectToSelected()
    {
        // Guard against a second connect attempt (e.g. a rapid double-click) racing
        // this one: without it, the second call disposes the first's still-connecting
        // IrcConnection out from under it.
        if (_connecting) return;

        int idx = _connList.SelectedIndex;
        if (idx < 0) return;
        var c = _savedConnections[idx];

        _settings.LastConnectionName = c.Name;
        SettingsStore.Save(_settings);

        _pendingAutoJoinChannels = [.. c.Channels
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

        _connecting = true;
        _connectSavedBtn.Enabled = false;
        try
        {
            await ConnectAsync(c);
        }
        finally
        {
            _connecting = false;
            _connectSavedBtn.Enabled = _connList.SelectedIndex >= 0;
        }
    }

    private void AddChannelTab(string name)
    {
        var log = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.FromArgb(20, 20, 30),
            ForeColor = Color.LightGray,
            Font = LogFontFor(name),
            ScrollBars = RichTextBoxScrollBars.Vertical,
            WordWrap = true
        };
        // While stacked, clicking into a pane's log makes it the message target
        log.MouseDown += (s, e) =>
        {
            if (InSplitMode && _splitChannels.Contains(name, StringComparer.OrdinalIgnoreCase))
                SetSplitCurrentTarget(name);
        };
        RouteTypingToInput(log);
        // Per-window header: "<name>     <nick> @ <server>     <topic>"
        var header = new Label
        {
            Text = ComposeHeader(name),
            Dock = DockStyle.Top,
            Height = LogicalToDeviceUnits(20),
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.FromArgb(45, 45, 60),
            ForeColor = Color.LightGray,
            Padding = new Padding(LogicalToDeviceUnits(4), 0, 0, 0),
            AutoEllipsis = true
        };
        // Side list of everyone in the channel. The "(server)" window has no
        // membership, so it gets no list.
        var nicks = new ListBox
        {
            Dock = DockStyle.Right,
            Width = LogicalToDeviceUnits(150),
            BackColor = Color.FromArgb(28, 28, 40),
            ForeColor = Color.LightGray,
            Font = LogFontFor(name),
            BorderStyle = BorderStyle.None,
            IntegralHeight = false,
            SelectionMode = SelectionMode.MultiExtended,
            Visible = IsChannel(name)
        };
        BuildNickMenu(name, nicks);
        var nickSplit = new Splitter
        {
            Dock = DockStyle.Right,
            Width = LogicalToDeviceUnits(4),
            BackColor = Color.FromArgb(45, 45, 60),
            MinExtra = LogicalToDeviceUnits(120),
            MinSize = LogicalToDeviceUnits(80),
            Visible = IsChannel(name)
        };
        // Docking is applied in reverse z-order, so the log must be added first
        // to end up filling whatever the list and splitter leave behind.
        var body = new Panel { Dock = DockStyle.Fill };
        body.Controls.Add(log);
        body.Controls.Add(nickSplit);
        body.Controls.Add(nicks);

        var tab = new TabPage(name);
        tab.Controls.Add(body);
        tab.Controls.Add(header);
        _tabs.TabPages.Add(tab);
        _channels[name] = (tab, header, log, nicks, body);
        RefreshNickList(name);
    }

    private static bool IsChannel(string name) => name.StartsWith('#') || name.StartsWith('&');

    // Right-click menu over a channel's nick list, acting on every selected nick.
    private void BuildNickMenu(string channel, ListBox nicks)
    {
        var menu = new ContextMenuStrip();
        var opItem = new ToolStripMenuItem("Op", null, (s, e) => ModeSelected(channel, 'o', true));
        var deopItem = new ToolStripMenuItem("Deop", null, (s, e) => ModeSelected(channel, 'o', false));
        var kickItem = new ToolStripMenuItem("Kick", null, (s, e) => KickSelected(channel));
        menu.Items.Add(opItem);
        menu.Items.Add(deopItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(kickItem);

        // A right-click outside any row has nothing to act on; otherwise label
        // the items with what they are about to affect.
        menu.Opening += (s, e) =>
        {
            int n = nicks.SelectedItems.Count;
            if (n == 0) { e.Cancel = true; return; }
            var suffix = n == 1 ? $" {SelectedNicks(channel)[0]}" : $" {n} nicks";
            opItem.Text = "Op" + suffix;
            deopItem.Text = "Deop" + suffix;
            kickItem.Text = "Kick" + suffix;
        };

        // ListBox does not move the selection on a right-click, so do it here:
        // clicking inside an existing multi-selection keeps it, clicking a row
        // outside it selects just that row.
        nicks.MouseDown += (s, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            int i = nicks.IndexFromPoint(e.Location);
            if (i < 0) { nicks.ClearSelected(); return; }
            if (!nicks.SelectedIndices.Contains(i))
            {
                nicks.ClearSelected();
                nicks.SetSelected(i, true);
            }
        };
        nicks.ContextMenuStrip = menu;
    }

    // Selected nicks with their @ / + status prefix stripped back off
    private List<string> SelectedNicks(string channel) =>
        _channels.TryGetValue(channel, out var ch)
            ? ch.nicks.SelectedItems.Cast<string>()
                  .Select(s => s.TrimStart('@', '+'))
                  .Where(s => s.Length > 0)
                  .ToList()
            : [];

    private void ModeSelected(string channel, char mode, bool adding)
    {
        var targets = SelectedNicks(channel);
        if (targets.Count == 0) return;
        // Servers cap how many mode changes one MODE command may carry (the
        // MODES token in 005; 4 is the common floor), so send them in batches.
        const int perCommand = 4;
        for (int i = 0; i < targets.Count; i += perCommand)
        {
            var batch = targets.Skip(i).Take(perCommand).ToArray();
            var flags = (adding ? "+" : "-") + new string(mode, batch.Length);
            _ = _irc?.SendRawAsync($"MODE {channel} {flags} {string.Join(' ', batch)}");
        }
    }

    private void KickSelected(string channel)
    {
        var targets = SelectedNicks(channel);
        if (targets.Count == 0) return;
        // Kicking several people at once is easy to trigger by accident from a
        // stray drag-selection, so confirm anything beyond a single nick.
        if (targets.Count > 1 &&
            MessageBox.Show(this, $"Kick these {targets.Count} users from {channel}?\n\n{string.Join(", ", targets)}",
                "Confirm kick", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        foreach (var t in targets)
            _ = _irc?.SendRawAsync($"KICK {channel} {t} :{_irc?.CurrentNick}");
    }

    // Rebuilds a channel's side list: ops first, then voiced, then everyone
    // else, alphabetical within each group.
    private void RefreshNickList(string channel)
    {
        if (!_channels.TryGetValue(channel, out var ch) || !IsChannel(channel)) return;
        var users = _channelUsers.TryGetValue(channel, out var u) ? u : [];
        var ordered = users
            .OrderBy(kv => kv.Value.Contains('o') ? 0 : kv.Value.Contains('v') ? 1 : 2)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => (kv.Value.Contains('o') ? "@" : kv.Value.Contains('v') ? "+" : "") + kv.Key)
            .ToArray();

        // Preserve the selection and scroll position across the rebuild, which
        // runs on every join/part/mode change. Selection is tracked by bare
        // nick, so someone being opped mid-selection stays selected even though
        // their "@" prefix just changed.
        var selected = ch.nicks.SelectedItems.Cast<string>()
            .Select(s => s.TrimStart('@', '+'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        int top = ch.nicks.TopIndex;
        ch.nicks.BeginUpdate();
        ch.nicks.Items.Clear();
        ch.nicks.Items.AddRange(ordered);
        if (selected.Count > 0)
            for (int i = 0; i < ordered.Length; i++)
                if (selected.Contains(ordered[i].TrimStart('@', '+')))
                    ch.nicks.SetSelected(i, true);
        if (top > 0 && top < ch.nicks.Items.Count) ch.nicks.TopIndex = top;
        ch.nicks.EndUpdate();
    }

    // Log panes are read-only, so typing into one is always meant for the input
    // line. Hand the keystroke over rather than dropping it. Modifier combos
    // (Ctrl+C, Ctrl+A) and navigation keys are left alone so the user can still
    // select and copy text out of the log.
    private void RouteTypingToInput(RichTextBox log)
    {
        log.KeyPress += (s, e) =>
        {
            if (char.IsControl(e.KeyChar)) return;
            if ((ModifierKeys & (Keys.Control | Keys.Alt)) != 0) return;
            _inputBox.Focus();
            _inputBox.AppendText(e.KeyChar.ToString());
            e.Handled = true;
        };
    }

    private string ComposeHeader(string name)
    {
        var gap = new string(' ', 5);
        var text = name;
        if (_irc is { IsConnected: true, CurrentNick: not null } && _activeServer != null)
            text += $"{gap}{_irc.CurrentNick} @ {_activeServer}";
        if (_channelModes.TryGetValue(name, out var modes) && modes.Length > 0)
            text += $"{gap}{modes}";
        if (_topics.TryGetValue(name, out var topic) && topic.Length > 0)
            text += $"{gap}{topic}";
        return text;
    }

    // Refreshes every window's header (tab views and split panes alike)
    private void UpdateAllHeaders()
    {
        foreach (var (name, ch) in _channels)
            ch.header.Text = ComposeHeader(name);
        foreach (var (name, label) in _splitHeaders)
            label.Text = ComposeHeader(name);
    }

    private void AppendLine(string target, string text, Color? color = null)
    {
        if (!_channels.TryGetValue(target, out var ch))
        {
            AddChannelTab(target);
            ch = _channels[target];
        }

        var log = ch.log;
        var ts = DateTime.Now.ToString("HH:mm");
        log.SelectionStart = log.TextLength;
        log.SelectionLength = 0;
        log.SelectionColor = Color.Gray;
        log.AppendText($"[{ts}] ");
        log.SelectionColor = color ?? Color.LightGray;
        log.AppendText(text + "\n");
        log.ScrollToCaret();

        // Highlight the tab if this message landed somewhere the user isn't
        // looking; every stacked pane is visible, so those never count as unread.
        if (!target.Equals(_currentTarget, StringComparison.OrdinalIgnoreCase)
            && !_splitChannels.Contains(target, StringComparer.OrdinalIgnoreCase)
            && _unreadTabs.Add(target))
            _tabs.Invalidate();

        WriteToLogFile(target, text);
    }

    // Appends the line to <LogDirectory>\<window>.log when a log directory is
    // configured (File > Options > Log). Failures are swallowed: logging must
    // never take the client down mid-conversation.
    private bool IsWindowLoggingStopped(string name) =>
        _settings.LoggingDisabledWindows.Any(w => w.Equals(name, StringComparison.OrdinalIgnoreCase));

    private void WriteToLogFile(string target, string text)
    {
        var dir = _settings.LogDirectory;
        if (!_settings.LoggingEnabled || string.IsNullOrEmpty(dir) || IsWindowLoggingStopped(target)) return;
        try
        {
            Directory.CreateDirectory(dir);
            var safeName = string.Join("_", target.Split(Path.GetInvalidFileNameChars()));
            File.AppendAllText(Path.Combine(dir, safeName + ".log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}");
        }
        catch { }
    }

    private async Task ConnectAsync(SavedConnection c)
    {
        // Connecting to a DIFFERENT server: the old server's channel windows are
        // stale, so close them. Same-server connects (auto-reconnect, manual
        // retry) keep their windows and scrollback.
        if (_windowsServer != null && !_windowsServer.Equals(c.Server, StringComparison.OrdinalIgnoreCase))
            CloseAllChannelWindows();
        _windowsServer = c.Server;

        _explicitQuit = false; // a fresh connection deserves a clean quit again
        _irc?.Dispose();
        var conn = new IrcConnection();
        _irc = conn;
        conn.MessageReceived += OnMessage;
        conn.Disconnected += () =>
        {
            // Dispose() doesn't unsubscribe event handlers, and this callback is
            // delivered via a posted continuation, so a superseded connection's
            // Disconnected can still fire after _irc has moved on to a newer one.
            // This guard also means user-initiated disconnects (OnDisconnect nulls
            // _irc first) never reach the auto-reconnect below — only real drops do.
            // _closing: the QUIT sent while the window closes must not trigger a
            // status update or auto-reconnect against a disposing form.
            if (_closing || _irc != conn) return;
            _statusLabel.Text = "Disconnected";
            _disconnectBtn.Enabled = false;
            _activeServer = null;
            UpdateAllHeaders();
            AppendLine("(server)", "*** Disconnected", Color.Orange);
            if (_settings.ReconnectOnDisconnect)
                _ = ReconnectAsync(conn, c);
        };

        try
        {
            AppendLine("(server)", $"*** Connecting to {c.Server}:{c.Port}...", Color.Cyan);
            await conn.ConnectAsync(c.Server, c.Port, c.Nick,
                string.IsNullOrWhiteSpace(c.Password) ? null : c.Password);
            _statusLabel.Text = $"Connected to {c.Server} as {c.Nick}";
            _disconnectBtn.Enabled = true;
            _activeServer = c.Server;
            UpdateAllHeaders();
        }
        catch (Exception ex)
        {
            AppendLine("(server)", $"*** Error: {ex.Message}", Color.Red);
        }
    }

    private async Task ReconnectAsync(IrcConnection failedConn, SavedConnection c)
    {
        // Rejoin everything that was open, not just the saved auto-join list —
        // the user may have joined more channels during the session.
        _pendingAutoJoinChannels = [.. _channels.Keys.Where(k => k.StartsWith('#') || k.StartsWith('&'))];

        AppendLine("(server)", "*** Reconnecting in 5 seconds...", Color.Cyan);
        await Task.Delay(5000);

        // Skip if the app is shutting down, or the user connected somewhere else
        // or clicked Disconnect while we were waiting — _irc no longer points at
        // the connection that died.
        if (_closing || _irc != failedConn) return;

        await ConnectAsync(c);
    }

    private async void OnDisconnect(object? s, EventArgs e)
    {
        if (_irc == null) return;
        try { await _irc.QuitAsync(QuitMessage); }
        catch { } // connection may already be dead; QUIT is best-effort
        _irc?.Dispose();
        _irc = null;
        _disconnectBtn.Enabled = false;
        // Update the UI here rather than relying on the Disconnected event:
        // that handler ignores events from connections that are no longer
        // current, and _irc is already null by the time its callback runs.
        _statusLabel.Text = "Disconnected";
        _activeServer = null;
        UpdateAllHeaders();
        AppendLine("(server)", "*** Disconnected", Color.Orange);
    }

    private Dictionary<string, string> UsersOf(string channel) =>
        _channelUsers.TryGetValue(channel, out var set)
            ? set
            : _channelUsers[channel] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // NAMES entries carry status prefixes: @ (op, also ~ owner / & admin) and
    // + (voice). Returns (nick, flags).
    private static (string nick, string flags) ParseNamesEntry(string raw)
    {
        var flags = "";
        int i = 0;
        while (i < raw.Length && "@+%&~".IndexOf(raw[i]) >= 0)
        {
            if (raw[i] is '@' or '&' or '~' && !flags.Contains('o')) flags += "o";
            else if (raw[i] == '+' && !flags.Contains('v')) flags += "v";
            i++;
        }
        return (raw[i..], flags);
    }

    private static void SetUserFlag(Dictionary<string, string> users, string nick, char flag, bool on)
    {
        users.TryGetValue(nick, out var flags);
        flags ??= "";
        if (on && !flags.Contains(flag)) flags += flag;
        if (!on) flags = flags.Replace(flag.ToString(), "");
        users[nick] = flags;
    }

    // "@nick" for ops, "+nick" for voiced, bare nick otherwise
    private string DisplayNick(string target, string nick)
    {
        if (_channelUsers.TryGetValue(target, out var users) && users.TryGetValue(nick, out var flags))
        {
            if (flags.Contains('o')) return "@" + nick;
            if (flags.Contains('v')) return "+" + nick;
        }
        return nick;
    }

    private void OnMessage(IrcMessage msg)
    {
        switch (msg.Command)
        {
            case "001": // RPL_WELCOME
                AppendLine("(server)", $"*** {msg.Params.LastOrDefault()}", Color.LightGreen);
                // _irc can already be null here: MessageReceived is delivered via a posted
                // continuation (IrcConnection.ReadLoopAsync), so a Disconnect click can null
                // out _irc between the "001" being read off the socket and this running.
                if (_irc != null && _pendingAutoJoinChannels.Count > 0)
                {
                    foreach (var channel in _pendingAutoJoinChannels)
                        _ = _irc.JoinAsync(channel.StartsWith('#') || channel.StartsWith('&') ? channel : $"#{channel}");
                    _pendingAutoJoinChannels.Clear();
                }
                UpdateAllHeaders(); // server may have adjusted our nick during registration
                break;

            case "372": // RPL_MOTD
            case "375":
            case "376":
                AppendLine("(server)", msg.Params.LastOrDefault() ?? "", Color.DimGray);
                break;

            case "PRIVMSG":
            {
                var target = msg.Params[0];
                var text = msg.Params.Length > 1 ? msg.Params[1] : "";
                var nick = msg.PrefixNick ?? msg.Prefix ?? "?";
                var displayTarget = target.StartsWith('#') || target.StartsWith('&') ? target : nick;

                // CTCP: text wrapped in \u0001. Handle the common queries out of band.
                if (text.Length >= 2 && text[0] == '\u0001' && text[^1] == '\u0001')
                {
                    var ctcp = text.Trim('\u0001');
                    var verb = ctcp.Split(' ', 2)[0].ToUpperInvariant();
                    if (verb == "ACTION")
                    {
                        var action = ctcp.Length > 7 ? ctcp[7..] : "";
                        AppendLine(displayTarget, $"* {DisplayNick(displayTarget, nick)} {action}", Color.Plum);
                    }
                    else if (verb == "VERSION")
                    {
                        var reply = string.IsNullOrEmpty(_settings.CustomVersionReply)
                            ? $"jclient irc by j0ker {VersionString}"
                            : _settings.CustomVersionReply;
                        _ = _irc?.SendRawAsync($"NOTICE {nick} :\u0001VERSION {reply}\u0001");
                        AppendLine(displayTarget, $"*** CTCP VERSION request from {nick}", Color.DimGray);
                    }
                    else if (verb == "PING")
                    {
                        // Echo the payload verbatim so the requester can time the round trip
                        _ = _irc?.SendRawAsync($"NOTICE {nick} :\u0001{ctcp}\u0001");
                        AppendLine(displayTarget, $"*** CTCP PING request from {nick}", Color.DimGray);
                    }
                    else if (verb == "TIME")
                    {
                        _ = _irc?.SendRawAsync($"NOTICE {nick} :\u0001TIME {DateTime.Now:ddd MMM dd HH:mm:ss yyyy}\u0001");
                        AppendLine(displayTarget, $"*** CTCP TIME request from {nick}", Color.DimGray);
                    }
                    else
                    {
                        AppendLine(displayTarget, $"*** CTCP {verb} request from {nick}", Color.DimGray);
                    }
                    break;
                }

                // PM to us — show in their nick tab
                AppendLine(displayTarget, $"<{DisplayNick(displayTarget, nick)}> {text}", Color.White);
                break;
            }

            case "JOIN":
            {
                var channel = msg.Params[0];
                var nick = msg.PrefixNick ?? "";
                if (!_channels.ContainsKey(channel))
                    AddChannelTab(channel);
                // Our own join starts a fresh membership list (NAMES follows) and
                // queries the channel modes (server answers with 324); anyone
                // else's join adds them to the list with no status yet.
                if (nick.Equals(_irc?.CurrentNick, StringComparison.OrdinalIgnoreCase))
                {
                    UsersOf(channel).Clear();
                    _ = _irc?.SendRawAsync($"MODE {channel}");
                }
                UsersOf(channel)[nick] = "";
                RefreshNickList(channel);
                // Deliberately no _tabs.SelectedTab change here: tabs only switch
                // when the user clicks one. The unread highlight marks the new tab.
                AppendLine(channel, $"*** {nick} joined {channel}", Color.LightBlue);
                break;
            }

            case "PART":
            {
                var channel = msg.Params[0];
                var nick = msg.PrefixNick ?? "";
                var reason = msg.Params.Length > 1 ? msg.Params[1] : "";
                AppendLine(channel, $"*** {nick} left {channel} ({reason})", Color.LightSalmon);
                UsersOf(channel).Remove(nick);
                RefreshNickList(channel);
                // If it's us parting and the tab is still open (e.g. via /part command), close it
                if (nick.Equals(_irc?.CurrentNick, StringComparison.OrdinalIgnoreCase)
                    && _channels.TryGetValue(channel, out var ch))
                {
                    _channels.Remove(channel);
                    _unreadTabs.Remove(channel);
                    _ctrlSelectedTabs.Remove(channel);
                    _topics.Remove(channel);
                    _channelUsers.Remove(channel);
                    _channelModes.Remove(channel);
                    _tabs.TabPages.Remove(ch.tab);
                    if (_currentTarget.Equals(channel, StringComparison.OrdinalIgnoreCase))
                    {
                        _currentTarget = "(server)";
                        if (_channels.TryGetValue("(server)", out var srv))
                            _tabs.SelectedTab = srv.tab;
                    }
                    HandleTabRemovedFromSplit(channel);
                }
                break;
            }

            case "QUIT":
            {
                var nick = msg.PrefixNick ?? "";
                var reason = msg.Params.LastOrDefault() ?? "";
                // Only the channels the quitter was actually in see the message
                foreach (var (channel, users) in _channelUsers)
                    if (users.Remove(nick) && _channels.ContainsKey(channel))
                    {
                        AppendLine(channel, $"*** {nick} quit ({reason})", Color.DimGray);
                        RefreshNickList(channel);
                    }
                break;
            }

            case "NICK":
            {
                var oldNick = msg.PrefixNick ?? "";
                var newNick = msg.Params[0];
                foreach (var (channel, users) in _channelUsers)
                {
                    if (!users.TryGetValue(oldNick, out var flags)) continue;
                    users.Remove(oldNick);
                    users[newNick] = flags; // op/voice status follows the rename
                    RefreshNickList(channel);
                    if (_channels.ContainsKey(channel))
                        AppendLine(channel, $"*** {oldNick} is now {newNick}", Color.Plum);
                }
                UpdateAllHeaders(); // IrcConnection tracks our own nick changes
                break;
            }

            case "353": // RPL_NAMREPLY
            {
                var channel = msg.Params.Length > 2 ? msg.Params[2] : "";
                var names = msg.Params.LastOrDefault() ?? "";
                if (channel.Length > 0)
                {
                    foreach (var n in names.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var (nick, flags) = ParseNamesEntry(n);
                        if (nick.Length > 0) UsersOf(channel)[nick] = flags;
                    }
                    RefreshNickList(channel);
                }
                AppendLine(channel, $"*** Users: {names}", Color.DimGray);
                break;
            }

            case "MODE":
            {
                var target = msg.Params[0];
                if (!target.StartsWith('#') && !target.StartsWith('&')) break; // ignore user modes
                var modes = msg.Params.Length > 1 ? msg.Params[1] : "";
                var users = UsersOf(target);
                bool adding = true;
                int argIdx = 2;
                foreach (var m in modes)
                {
                    if (m == '+') { adding = true; continue; }
                    if (m == '-') { adding = false; continue; }
                    // modes that consume an argument (RFC 2812 §3.2.3 + common ircd extras)
                    bool takesArg = m is 'o' or 'v' or 'h' or 'b' or 'e' or 'I' or 'k' || (m == 'l' && adding);
                    string? arg = takesArg && argIdx < msg.Params.Length ? msg.Params[argIdx++] : null;
                    if (arg == null) continue;
                    if (m == 'o') SetUserFlag(users, arg, 'o', adding);
                    else if (m == 'v') SetUserFlag(users, arg, 'v', adding);
                }
                RefreshNickList(target);
                AppendLine(target, $"*** {msg.PrefixNick ?? msg.Prefix ?? "server"} sets mode {string.Join(" ", msg.Params.Skip(1))}", Color.LightBlue);
                // Re-query rather than replaying mode arithmetic locally: the 324
                // reply is authoritative and refreshes the header's mode display.
                _ = _irc?.SendRawAsync($"MODE {target}");
                break;
            }

            case "332": // RPL_TOPIC — sent on join when the channel has a topic
            {
                var channel = msg.Params.Length > 1 ? msg.Params[1] : "";
                var topic = msg.Params.LastOrDefault() ?? "";
                if (channel.Length > 0)
                {
                    _topics[channel] = topic;
                    AppendLine(channel, $"*** Topic: {topic}", Color.DimGray);
                    UpdateAllHeaders();
                }
                break;
            }

            case "324": // RPL_CHANNELMODEIS — reply to our MODE query
            {
                var channel = msg.Params.Length > 1 ? msg.Params[1] : "";
                if (channel.Length > 0)
                {
                    _channelModes[channel] = string.Join(" ", msg.Params.Skip(2));
                    UpdateAllHeaders();
                }
                break;
            }

            case "367": // RPL_BANLIST — one ban mask; params: [me, chan, mask, whoset, when]
            {
                var channel = msg.Params.Length > 1 ? msg.Params[1] : "";
                var mask = msg.Params.Length > 2 ? msg.Params[2] : "";
                if (_channelSettingsForm != null && mask.Length > 0
                    && channel.Equals(_channelSettingsChannel, StringComparison.OrdinalIgnoreCase))
                    _channelSettingsForm.AddBan(mask);
                break;
            }

            case "368": // RPL_ENDOFBANLIST — nothing more to collect
                break;

            case "331": // RPL_NOTOPIC
            {
                var channel = msg.Params.Length > 1 ? msg.Params[1] : "";
                if (channel.Length > 0 && _topics.Remove(channel))
                    UpdateAllHeaders();
                break;
            }

            case "TOPIC": // someone changed the topic
            {
                var channel = msg.Params[0];
                var topic = msg.Params.Length > 1 ? msg.Params[1] : "";
                if (topic.Length > 0) _topics[channel] = topic;
                else _topics.Remove(channel);
                AppendLine(channel, $"*** {msg.PrefixNick ?? "server"} set topic: {topic}", Color.Plum);
                UpdateAllHeaders();
                break;
            }

            case "NOTICE":
            {
                var text = msg.Params.LastOrDefault() ?? "";
                var nick = msg.PrefixNick ?? msg.Prefix ?? "server";
                AppendLine("(server)", $"-{nick}- {text}", Color.Gold);
                break;
            }

            case "KICK":
            {
                var channel = msg.Params[0];
                var kicked = msg.Params[1];
                var reason = msg.Params.Length > 2 ? msg.Params[2] : "";
                AppendLine(channel, $"*** {msg.PrefixNick} kicked {kicked} ({reason})", Color.OrangeRed);
                UsersOf(channel).Remove(kicked);
                RefreshNickList(channel);
                break;
            }

            // ERR_NOTREGISTERED — server requires NickServ/SASL (e.g. Libera Chat)
            case "451":
                AppendLine("(server)", "*** Server requires registration. Try a different server (e.g. irc.rizon.net) or register your nick.", Color.Orange);
                break;

            // ERR_NICKNAMEINUSE
            case "433":
                AppendLine("(server)", $"*** Nick already in use: {msg.Params.LastOrDefault()}", Color.Orange);
                break;

            // ERR_BADCHANNELKEY / ERR_INVITEONLYCHAN / ERR_BANNEDFROMCHAN
            case "475": case "473": case "474":
                AppendLine("(server)", $"*** Cannot join channel: {string.Join(" ", msg.Params.Skip(1))}", Color.Orange);
                break;

            default:
                // Show numeric replies and unknown commands in server tab
                if (int.TryParse(msg.Command, out _))
                    AppendLine("(server)", $"[{msg.Command}] {string.Join(" ", msg.Params)}", Color.DimGray);
                break;
        }
    }

    private async void OnSend(object? s, EventArgs e)
    {
        var text = _inputBox.Text.Trim();
        _inputBox.Clear();
        if (string.IsNullOrEmpty(text)) return;

        // Record in command history (skip consecutive duplicates) and reset
        // the Up/Down browse position to "past the newest entry".
        if (_inputHistory.Count == 0 || _inputHistory[^1] != text)
            _inputHistory.Add(text);
        _historyIndex = _inputHistory.Count;
        _historyDraft = "";

        if (_irc == null) return;

        if (text.StartsWith('/'))
        {
            await HandleCommand(text[1..]);
        }
        else
        {
            if (_currentTarget is "(server)" or "") return;
            await _irc.PrivMsgAsync(_currentTarget, text);
            AppendLine(_currentTarget, $"<{DisplayNick(_currentTarget, _irc.CurrentNick ?? "")}> {text}", Color.LightYellow);
        }
    }

    private async Task HandleCommand(string cmd, int depth = 0)
    {
        if (_irc == null) return;
        var parts = cmd.Split(' ', 2);
        var verb = parts[0].ToUpperInvariant();
        var rest = parts.Length > 1 ? parts[1] : "";

        // User-defined aliases take precedence over built-ins. Guard against
        // runaway recursion (an alias that expands to itself).
        if (depth < 10 && _aliases.TryGetValue(parts[0], out var template))
        {
            var args = rest.Length > 0 ? rest.Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];
            var expanded = ExpandAlias(template, args);
            if (expanded == null)
            {
                AppendLine(_currentTarget, $"*** Alias /{parts[0]}: missing required parameter", Color.OrangeRed);
                return;
            }
            // Each |-separated piece is run as its own command
            foreach (var piece in expanded.Split('|'))
            {
                var line = piece.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith('/')) await HandleCommand(line[1..], depth + 1);
                else if (_currentTarget is not "(server)" and not "")
                {
                    await _irc.PrivMsgAsync(_currentTarget, line);
                    AppendLine(_currentTarget, $"<{DisplayNick(_currentTarget, _irc.CurrentNick ?? "")}> {line}", Color.LightYellow);
                }
            }
            return;
        }

        switch (verb)
        {
            case "JOIN":
                await _irc.JoinAsync(rest);
                break;
            case "PART":
            {
                var args = rest.Split(' ', 2);
                await _irc.PartAsync(args[0], args.Length > 1 ? args[1] : null);
                break;
            }
            case "MSG":
            case "QUERY":
            {
                var args = rest.Split(' ', 2);
                if (args.Length == 2)
                {
                    await _irc.PrivMsgAsync(args[0], args[1]);
                    AppendLine(args[0], $"<{DisplayNick(args[0], _irc.CurrentNick ?? "")}> {args[1]}", Color.LightYellow);
                }
                break;
            }
            case "NICK":
                await _irc.SendRawAsync($"NICK {rest}");
                break;
            case "QUIT":
                // An explicit /quit message wins over the default; either way,
                // the window-close path must not send a second QUIT afterwards.
                _explicitQuit = true;
                await _irc.QuitAsync(rest.Length > 0 ? rest : QuitMessage);
                break;
            case "TOPIC":
            {
                var args = rest.Split(' ', 2);
                await _irc.SendRawAsync(args.Length > 1 ? $"TOPIC {args[0]} :{args[1]}" : $"TOPIC {args[0]}");
                break;
            }
            case "ME":
                // CTCP ACTION — RFC 1459 CTCP extension
                if (_currentTarget is not "(server)" and not "")
                {
                    var action = $"\u0001ACTION {rest}\u0001";
                    await _irc.PrivMsgAsync(_currentTarget, action);
                    AppendLine(_currentTarget, $"* {DisplayNick(_currentTarget, _irc.CurrentNick ?? "")} {rest}", Color.Plum);
                }
                break;
            case "RAW":
                await _irc.SendRawAsync(rest);
                break;
            default:
                await _irc.SendRawAsync(cmd);
                break;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Rest the caret on the input line (the server tab is active at startup)
        // so the user can type straight away without clicking into the box first.
        _inputBox.Focus();
        if (!_settings.ConnectOnStartup || _savedConnections.Count == 0) return;

        var c = _savedConnections.FirstOrDefault(x => x.Name == _settings.LastConnectionName)
                ?? _savedConnections[0];
        _connList.SelectedIndex = _savedConnections.IndexOf(c);
        ConnectToSelected();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _closing = true;
        // Closing the window by any means sends a proper "QUIT :jclient" so the
        // server sees a clean quit rather than a dropped socket — unless the user
        // already issued an explicit /quit, whose message must stand.
        if (!_explicitQuit && _irc is { IsConnected: true })
        {
            try { _irc.QuitAsync(QuitMessage).Wait(500); }
            catch { }
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _irc?.Dispose();
        base.OnFormClosed(e);
    }
}
