using System.Diagnostics;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace IRCClient;

// How stacked windows are arranged.
public enum StackLayout
{
    Horizontal, // side by side, one column each
    Vertical,   // one above the other, full width
    Tile,       // a grid, as square as the count allows
    Layered     // cascaded and overlapping, click one to raise it
}

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

    // True from the moment a connection drops until the next one is up, which
    // is what reddens the tab labels. It stays false before the first connect:
    // never having connected is not the same as having lost one.
    private bool _connectionLost;

    private void SetConnectionLost(bool lost)
    {
        if (_connectionLost == lost) return;
        _connectionLost = lost;
        _tabs.Invalidate();
    }

    // The window a query command (/who, /whois, /list, /raw, ...) was typed in.
    // Server replies to it are printed there instead of the server tab, and are
    // cleared once the reply's end-of-list numeric arrives so later unsolicited
    // numerics fall back to "(server)".
    private string? _replyTarget;

    // Numerics that terminate a query reply: ENDOFWHO/WHOIS/WHOWAS, LISTEND,
    // ENDOFSTATS/NAMES/LINKS/BANLIST/INFO/MOTD/USERS.
    private static readonly HashSet<string> ReplyEndNumerics =
        ["315", "318", "323", "369", "219", "366", "365", "368", "374", "376", "394"];

    // Commands whose replies belong in the window they were sent from.
    private static readonly HashSet<string> QueryCommands =
        [
            "WHO", "WHOIS", "WHOWAS", "LIST", "NAMES", "LINKS", "STATS", "MAP",
            "MOTD", "LUSERS", "VERSION", "TIME", "ADMIN", "INFO", "TRACE",
            "USERHOST", "ISON", "RAW", "QUOTE"
        ];

    // Channel modes (e.g. "+tn", plus key/limit args), from 324 replies to the
    // MODE query sent on join and re-queried after any mode change; shown in
    // the window header between server and topic.
    private readonly Dictionary<string, string> _channelModes = new(StringComparer.OrdinalIgnoreCase);

    // Who is in each channel (from NAMES on join, then joins/parts/kicks/modes),
    // mapping nick -> mode flags ("o" op, "v" voice, "ov", or ""). Used to scope
    // quit/nick messages to the right channels and to prefix speakers' nicks.
    private readonly Dictionary<string, Dictionary<string, string>> _channelUsers = new(StringComparer.OrdinalIgnoreCase);

    // Channels whose creation time has already been announced. The server sends
    // 329 alongside every 324, and we query modes again after each mode change,
    // so without this the same line would repeat all session.
    private readonly HashSet<string> _creationShown = new(StringComparer.OrdinalIgnoreCase);

    // Input command history, browsed with Up/Down. _historyIndex ==
    // _inputHistory.Count means "past the newest entry" (the live draft).
    private readonly List<string> _inputHistory = [];
    private int _historyIndex;
    private string _historyDraft = "";

    // Whether Up has actually started a browse. Down does nothing until it has,
    // so a freshly typed line can never be replaced by a stashed draft.
    private bool _browsingHistory;

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
    private StackLayout _splitLayout;

    // Host for Layered mode, where panes are positioned by hand instead of by
    // the table: it sits in the table's single cell and holds the cascade.
    private Panel? _layerHost;
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

    // Ignore masks, compiled from the raw multi-line setting. Each entry matches
    // against a full "nick!user@host" prefix.
    private List<Regex> _ignores = [];

    // Current masks as a list, comments and blanks included so editing from
    // /ignore doesn't throw away anything typed in Tools > Ignore.
    private List<string> IgnoreLines() =>
        [.. _settings.IgnoreMasks.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0)];

    private void SaveIgnores(List<string> lines)
    {
        _settings.IgnoreMasks = string.Join("\n", lines);
        SettingsStore.Save(_settings);
        ParseIgnores();
    }

    // Two masks are the same entry if they match once a bare nick is expanded,
    // so "/ignore bob" then "/ignore -r bob!*@*" removes what it looks like.
    private static string NormalizeMask(string mask)
    {
        mask = mask.Trim();
        if (mask.Length > 0 && !mask.Contains('!') && !mask.Contains('@')) mask += "!*@*";
        return mask;
    }

    // Whether this exact nick has an entry, i.e. whether the menu's Ignore item
    // should read as "stop". A wildcard mask that happens to cover them counts
    // as ignored too, since that is what the user sees happening.
    private bool IsNickIgnored(string nick) =>
        IgnoreLines().Any(l => NormalizeMask(l).Equals(NormalizeMask(nick), StringComparison.OrdinalIgnoreCase))
        || _ignores.Any(r => r.IsMatch($"{nick}!*@*"));

    private void AddIgnores(string args)
    {
        var lines = IgnoreLines();
        var masks = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // No argument: report what is currently ignored
        if (masks.Length == 0)
        {
            var live = lines.Where(l => !l.StartsWith('#')).ToList();
            if (live.Count == 0)
                AppendLine(_currentTarget, "*** Ignore list is empty", Color.Cyan);
            else
                foreach (var l in live)
                    AppendLine(_currentTarget, $"*** Ignoring {l}", Color.Cyan);
            return;
        }

        foreach (var raw in masks)
        {
            var mask = NormalizeMask(raw);
            if (lines.Any(l => NormalizeMask(l).Equals(mask, StringComparison.OrdinalIgnoreCase)))
            {
                AppendLine(_currentTarget, $"*** Already ignoring {mask}", Color.Cyan);
                continue;
            }
            lines.Add(mask);
            AppendLine(_currentTarget, $"*** Ignoring {mask}", Color.Cyan);
        }
        SaveIgnores(lines);
    }

    private void RemoveIgnores(string args)
    {
        var masks = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (masks.Length == 0)
        {
            AppendLine(_currentTarget, "*** Usage: /ignore -r <mask>", Color.OrangeRed);
            return;
        }

        var lines = IgnoreLines();
        foreach (var raw in masks)
        {
            var mask = NormalizeMask(raw);
            int removed = lines.RemoveAll(l =>
                NormalizeMask(l).Equals(mask, StringComparison.OrdinalIgnoreCase));
            AppendLine(_currentTarget,
                removed > 0 ? $"*** No longer ignoring {mask}" : $"*** Not ignoring {mask}",
                removed > 0 ? Color.Cyan : Color.OrangeRed);
        }
        SaveIgnores(lines);
    }

    private void ParseIgnores()
    {
        _ignores = [];
        foreach (var raw in _settings.IgnoreMasks.Split('\n'))
        {
            var mask = raw.Trim();
            if (mask.Length == 0 || mask.StartsWith('#')) continue;
            // A bare nick ignores that nick from anywhere
            if (!mask.Contains('!') && !mask.Contains('@')) mask += "!*@*";
            // Escape everything, then re-open the two wildcards
            var pattern = "^" + Regex.Escape(mask).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            try { _ignores.Add(new Regex(pattern, RegexOptions.IgnoreCase)); }
            catch (ArgumentException) { } // unusable mask: skip it rather than lose the rest
        }
    }

    // True when a message's sender matches the ignore list. Servers send a bare
    // server name as prefix, which no nick!user@host mask can match, so server
    // messages are never ignored by accident.
    private bool IsIgnored(IrcMessage msg)
    {
        if (_ignores.Count == 0) return false;
        var prefix = msg.Prefix;
        if (string.IsNullOrEmpty(prefix)) return false;
        // Match the full prefix, and also nick!*@* so a bare-nick mask hits
        // even when the server sends an unusual prefix form.
        var nick = msg.PrefixNick ?? "";
        return _ignores.Any(r => r.IsMatch(prefix) || (nick.Length > 0 && r.IsMatch($"{nick}!*@*")));
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
        _creationShown.Clear();
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
    // A RichTextBox rather than a TextBox so misspellings can be marked in
    // place. Single-line, and with the built-in URL detection off, it behaves
    // like the plain box it replaced.
    private readonly RichTextBox _inputBox = new()
    {
        Dock = DockStyle.Fill,
        Font = new Font("Consolas", 10),
        Multiline = false,
        DetectUrls = false,
        ScrollBars = RichTextBoxScrollBars.None,
        BorderStyle = BorderStyle.Fixed3D
    };
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
        Text = $"jclient irc for Windows {VersionString}";
        Font = new Font("Segoe UI", 9);
        Size = LogicalToDeviceUnits(new Size(900, 650));
        MinimumSize = LogicalToDeviceUnits(new Size(600, 400));
        Icon = AppIcon.Get();
        RestoreWindowPlacement();

        // Typing anywhere in the main window goes to the input line, whichever
        // control happens to hold focus (a log, the nick list, the connection
        // list, a button). See OnKeyPress.
        KeyPreview = true;

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
        var minimizeToTrayItem = new ToolStripMenuItem("Keep running when closed (minimize to tray)")
        {
            CheckOnClick = true,
            Checked = _settings.MinimizeToTrayOnClose
        };
        minimizeToTrayItem.CheckedChanged += (s, e) =>
        {
            _settings.MinimizeToTrayOnClose = minimizeToTrayItem.Checked;
            SettingsStore.Save(_settings);
        };
        var keepAliveItem = new ToolStripMenuItem("Keep alive (PING every 60s)")
        {
            CheckOnClick = true,
            Checked = _settings.KeepAlive
        };
        keepAliveItem.CheckedChanged += (s, e) =>
        {
            _settings.KeepAlive = keepAliveItem.Checked;
            SettingsStore.Save(_settings);
            UpdateKeepAliveTimer();
        };
        connectOptions.DropDownItems.Add(connectOnStartupItem);
        connectOptions.DropDownItems.Add(reconnectOnDisconnectItem);
        connectOptions.DropDownItems.Add(keepAliveItem);
        connectOptions.DropDownItems.Add(minimizeToTrayItem);
        connectOptions.DropDownItems.Add(new ToolStripSeparator());
        connectOptions.DropDownItems.Add(versionReplyItem);
        optionsItem.DropDownItems.Add(connectOptions);

        var urlOptions = new ToolStripMenuItem("URL");
        var urlNoConfirmItem = new ToolStripMenuItem("Run URL without confirmation")
        {
            CheckOnClick = true,
            Checked = _settings.OpenUrlsWithoutConfirmation
        };
        urlNoConfirmItem.CheckedChanged += (s, e) =>
        {
            _settings.OpenUrlsWithoutConfirmation = urlNoConfirmItem.Checked;
            SettingsStore.Save(_settings);
        };
        urlOptions.DropDownItems.Add(urlNoConfirmItem);
        optionsItem.DropDownItems.Add(urlOptions);

        var floodOptions = new ToolStripMenuItem("Flood");
        var floodItem = new ToolStripMenuItem("Flood protection")
        {
            CheckOnClick = true,
            Checked = _settings.FloodProtection
        };
        floodItem.CheckedChanged += (s, e) =>
        {
            _settings.FloodProtection = floodItem.Checked;
            SettingsStore.Save(_settings);
            // Applies to the live connection straight away, not just the next one
            if (_irc != null) _irc.FloodProtection = floodItem.Checked;
        };
        floodOptions.DropDownItems.Add(floodItem);
        floodOptions.DropDownItems.Add(new ToolStripSeparator());
        floodOptions.DropDownItems.Add(new ToolStripMenuItem(
            "Paces your own lines: 5 at once, then 1 every 2 seconds") { Enabled = false });
        optionsItem.DropDownItems.Add(floodOptions);
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
        // Opens the configured log directory in File Explorer. Disabled until a
        // folder is set, since there is nothing to open otherwise.
        var openLogDirItem = new ToolStripMenuItem("Open Log Folder in Explorer");
        openLogDirItem.Click += (s, e) =>
        {
            var dir = _settings.LogDirectory;
            if (string.IsNullOrEmpty(dir)) return;
            try
            {
                Directory.CreateDirectory(dir); // may not exist yet if nothing has logged
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't open the log folder:\n{ex.Message}",
                    "Open Log Folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        logOptions.DropDownItems.Add(setLogDirItem);
        logOptions.DropDownItems.Add(new ToolStripSeparator());
        logOptions.DropDownItems.Add(logToggleItem);
        logOptions.DropDownItems.Add(new ToolStripSeparator());
        logOptions.DropDownItems.Add(openLogDirItem);
        // Grey the item out whenever the submenu opens with no folder configured
        logOptions.DropDownOpening += (s, e) =>
            openLogDirItem.Enabled = !string.IsNullOrEmpty(_settings.LogDirectory);
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
        var ignoreItem = new ToolStripMenuItem("Ignore...");
        ignoreItem.Click += (s, e) =>
        {
            using var dlg = new IgnoreEditForm(_settings.IgnoreMasks);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _settings.IgnoreMasks = dlg.Masks;
            SettingsStore.Save(_settings);
            ParseIgnores();
        };
        toolsMenu.DropDownItems.Add(aliasItem);
        toolsMenu.DropDownItems.Add(ignoreItem);
        _menu.Items.Add(toolsMenu);

        MainMenuStrip = _menu;

        // Drag bar between the connection library and the chat area. The nick
        // list has had one of these on the right since it was added; this gives
        // the left edge the same treatment.
        var librarySplit = new Splitter
        {
            Dock = DockStyle.Left,
            Width = LogicalToDeviceUnits(4),
            BackColor = Color.FromArgb(45, 45, 60),
            MinExtra = LogicalToDeviceUnits(300),
            MinSize = LogicalToDeviceUnits(120)
        };

        // Add order matters for docking: controls are docked in reverse of Controls.Add
        // order, so _mainLayout (Fill) is added first to claim whatever space is left
        // after the menu (Top), _libraryPanel (Left), and the status bar have claimed
        // theirs. The menu is added last so it docks first and spans the full width.
        Controls.Add(_mainLayout);
        Controls.Add(librarySplit);
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

                // While vertically stacked the strip is still there, but the
                // page area below it isn't: picking a window that isn't in the
                // stack has to unstack to show it. Picking one that is in the
                // stack just aims the input box at that pane.
                if (InSplitMode)
                {
                    var picked = _currentTarget;
                    if (_splitChannels.Contains(picked, StringComparer.OrdinalIgnoreCase))
                        SetSplitCurrentTarget(picked);
                    else
                        BeginInvoke(ExitSplit);
                }
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
            // A dropped connection reddens every tab until we are back on, and
            // outranks the unread highlight: nothing new can arrive while down.
            var color = _connectionLost ? Color.Red
                : _unreadTabs.Contains(tab.Text) ? Color.DarkOrange
                : _tabs.ForeColor;
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
            else if (e.Button == MouseButtons.Left)
            {
                // Start of a possible reorder drag
                _dragTabIndex = TabIndexAt(e.Location);
                _dragTabPage = _dragTabIndex >= 0 ? _tabs.TabPages[_dragTabIndex] : null;
            }
        };

        // Dragging a tab sideways moves it past its neighbours, one position at
        // a time, so the strip reorders under the cursor as you go.
        _tabs.MouseMove += (s, e) =>
        {
            if (_dragTabPage == null || e.Button != MouseButtons.Left) return;
            MoveDraggedTab(TabIndexAt(e.Location));
        };

        // The move is repeated on release because a TabControl does not reliably
        // raise MouseMove while a button is held: without this, a drag that
        // reported no movement would do nothing at all.
        _tabs.MouseUp += (s, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            MoveDraggedTab(TabIndexAt(e.Location));
            _dragTabPage = null;
            _dragTabIndex = -1;
        };

        // Right-click on a tab: stack options plus Close for the clicked tab
        TabPage? _rightClickedTab = null;
        var tabMenu = new ContextMenuStrip();
        var stackHItem = new ToolStripMenuItem("Stack Horizontal");
        stackHItem.Click += (s, e) => EnterSplit(StackLayout.Horizontal);
        var stackVItem = new ToolStripMenuItem("Stack Vertical");
        stackVItem.Click += (s, e) => EnterSplit(StackLayout.Vertical);
        var stackTileItem = new ToolStripMenuItem("Tile");
        stackTileItem.Click += (s, e) => EnterSplit(StackLayout.Tile);
        var stackLayerItem = new ToolStripMenuItem("Layered");
        stackLayerItem.Click += (s, e) => EnterSplit(StackLayout.Layered);
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
        tabMenu.Items.Add(stackTileItem);
        tabMenu.Items.Add(stackLayerItem);
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
        _splitMenu.Items.Add("Stack Horizontal", null, (s, e) => BuildSplit([.. _splitChannels], StackLayout.Horizontal));
        _splitMenu.Items.Add("Stack Vertical", null, (s, e) => BuildSplit([.. _splitChannels], StackLayout.Vertical));
        _splitMenu.Items.Add("Tile", null, (s, e) => BuildSplit([.. _splitChannels], StackLayout.Tile));
        _splitMenu.Items.Add("Layered", null, (s, e) => BuildSplit([.. _splitChannels], StackLayout.Layered));
        _splitMenu.Items.Add(new ToolStripSeparator());
        _splitMenu.Items.Add("Unstack", null, (s, e) => ExitSplit());

        // Ctrl+A selects all text in the input box; Ctrl+V and Shift+Insert are
        // taken over so a multi-line paste can be sent line by line.
        _inputBox.KeyDown += (s, e) =>
        {
            if (e.Control && e.KeyCode == Keys.A)
            {
                _inputBox.SelectAll();
                e.SuppressKeyPress = true;
            }
            else if ((e.Control && e.KeyCode == Keys.V) || (e.Shift && e.KeyCode == Keys.Insert))
            {
                e.SuppressKeyPress = true;
                PasteIntoInput();
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
                _browsingHistory = true;
                _inputBox.Text = _inputHistory[_historyIndex];
                _inputBox.SelectionStart = _inputBox.TextLength;
            }
            else if (e.KeyCode == Keys.Down)
            {
                e.SuppressKeyPress = true;
                // Walk forward through the history while there is one to walk;
                // past the newest entry — or on a line typed from scratch —
                // Down empties the entry bar.
                if (_browsingHistory && _historyIndex < _inputHistory.Count - 1)
                {
                    _historyIndex++;
                    _inputBox.Text = _inputHistory[_historyIndex];
                }
                else
                {
                    _inputBox.Clear();
                    _historyIndex = _inputHistory.Count;
                    _historyDraft = "";
                    _browsingHistory = false;
                }
                _inputBox.SelectionStart = _inputBox.TextLength;
            }
        };

        // Tab completes the nick being typed from the current channel's user
        // list. Tab is a focus-change key, so it has to be claimed in
        // PreviewKeyDown before KeyDown ever sees it.
        _inputBox.PreviewKeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Tab) e.IsInputKey = true;
        };
        _inputBox.KeyDown += (s, e) =>
        {
            if (e.KeyCode != Keys.Tab || e.Control || e.Alt) return;
            e.SuppressKeyPress = true;
            CompleteNick(e.Shift);
        };

        // Any other edit invalidates an in-progress completion cycle, so the
        // next Tab starts a fresh search rather than cycling stale matches.
        _inputBox.TextChanged += (s, e) =>
        {
            if (!_completing) _completeMatches.Clear();
        };

        // Right-click context menu on input box (cut/copy/paste/select all)
        var inputMenu = new ContextMenuStrip();
        // Held by name, not by index: spelling suggestions are inserted above
        // these, which would shift any index-based lookup onto the wrong item.
        var cutItem = new ToolStripMenuItem("Cut", null, (s, e) => _inputBox.Cut());
        var copyItem = new ToolStripMenuItem("Copy", null, (s, e) => _inputBox.Copy());
        var pasteItem = new ToolStripMenuItem("Paste", null, (s, e) => PasteIntoInput());
        inputMenu.Items.Add(cutItem);
        inputMenu.Items.Add(copyItem);
        inputMenu.Items.Add(pasteItem);
        inputMenu.Items.Add(new ToolStripSeparator());
        inputMenu.Items.Add("Select All", null, (s, e) => _inputBox.SelectAll());
        inputMenu.Opening += (s, e) =>
        {
            AddSpellingSuggestions(inputMenu);
            cutItem.Enabled = _inputBox.SelectionLength > 0;
            copyItem.Enabled = _inputBox.SelectionLength > 0;
            pasteItem.Enabled = Clipboard.ContainsText();
        };
        _inputBox.ContextMenuStrip = inputMenu;
        WireSpellCheck();

        // Apply persisted View settings
        TopMost = _settings.KeepOnTop;
        if (_settings.DefaultFontEnabled || _settings.ChannelFontEnabled) ApplyFonts();
        ParseAliases();
        ParseIgnores();
    }

    // The tab being dragged to a new position, and where it currently sits
    private TabPage? _dragTabPage;
    private int _dragTabIndex = -1;

    // Moves the dragged tab to the position under the cursor, if that is a
    // different tab than it already occupies.
    private void MoveDraggedTab(int over)
    {
        if (_dragTabPage == null || over < 0) return;
        int at = _tabs.TabPages.IndexOf(_dragTabPage);
        if (at < 0 || at == over) return;

        var page = _dragTabPage;
        _tabs.SuspendLayout();
        _tabs.TabPages.Remove(page);
        _tabs.TabPages.Insert(over, page);
        _tabs.SelectedTab = page;
        _tabs.ResumeLayout();
        _dragTabIndex = over;
    }

    private int TabIndexAt(Point p)
    {
        for (int i = 0; i < _tabs.TabCount; i++)
            if (_tabs.GetTabRect(i).Contains(p))
                return i;
        return -1;
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
        _creationShown.Remove(name);
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
    private void EnterSplit(StackLayout layout)
    {
        var all = _tabs.TabPages.Cast<TabPage>().Select(t => t.Text);
        var targets = _ctrlSelectedTabs.Count >= 2
            ? all.Where(_ctrlSelectedTabs.Contains).ToList()
            : all.ToList();
        _ctrlSelectedTabs.Clear();
        BuildSplit(targets, layout);
    }

    private void BuildSplit(List<string> channels, StackLayout layout)
    {
        TearDownSplitPanes();
        _splitChannels.Clear();
        _splitChannels.AddRange(channels.Where(_channels.ContainsKey));
        if (_splitChannels.Count == 0)
        {
            ExitSplit();
            return;
        }
        _splitLayout = layout;

        int n = _splitChannels.Count;
        // Tile aims for a grid as square as the count allows, filling rows
        // first: 4 windows make 2x2, 5 make 3x2 with one gap.
        int cols = layout switch
        {
            StackLayout.Horizontal => n,
            StackLayout.Vertical => 1,
            StackLayout.Tile => (int)Math.Ceiling(Math.Sqrt(n)),
            _ => 1 // Layered puts everything in one cell
        };
        int rows = layout switch
        {
            StackLayout.Horizontal => 1,
            StackLayout.Vertical => n,
            StackLayout.Tile => (int)Math.Ceiling((double)n / cols),
            _ => 1
        };

        _splitPanel.SuspendLayout();
        _splitPanel.ColumnStyles.Clear();
        _splitPanel.RowStyles.Clear();
        _splitPanel.ColumnCount = cols;
        _splitPanel.RowCount = rows;
        for (int i = 0; i < cols; i++)
            _splitPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / cols));
        for (int i = 0; i < rows; i++)
            _splitPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));

        // Layered mode positions its panes itself, so it needs a plain panel to
        // do it in — a table would just put each one in a cell.
        if (layout == StackLayout.Layered)
        {
            _layerHost = new Panel { Dock = DockStyle.Fill, ContextMenuStrip = _splitMenu };
            _layerHost.Resize += (s, e) => LayoutLayeredPanes();
            _splitPanel.Controls.Add(_layerHost, 0, 0);
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
            var pane = new Panel
            {
                // Layered panes are placed by hand; the rest fill their cell
                Dock = layout == StackLayout.Layered ? DockStyle.None : DockStyle.Fill,
                Margin = new Padding(LogicalToDeviceUnits(1)),
                ContextMenuStrip = _splitMenu,
                BorderStyle = layout == StackLayout.Layered ? BorderStyle.FixedSingle : BorderStyle.None
            };
            // Move the whole body so the nick list follows its log into the pane
            var body = _channels[name].body;
            body.Parent?.Controls.Remove(body);
            log.ContextMenuStrip = _splitMenu;
            pane.Controls.Add(body);
            pane.Controls.Add(header);
            _splitPanes.Add(pane);
            _splitHeaders[name] = header;
            if (layout == StackLayout.Layered)
            {
                _layerHost!.Controls.Add(pane);
                // Clicking anywhere in a buried window raises it and makes it
                // the one the input box talks to.
                var raiseName = name;
                var raisePane = pane;
                void Raise(object? s, EventArgs e)
                {
                    raisePane.BringToFront();
                    SetSplitCurrentTarget(raiseName);
                }
                pane.Click += Raise;
                header.Click += Raise;
                log.Click += Raise;
            }
            else
            {
                _splitPanel.Controls.Add(pane, i % cols, i / cols);
            }

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

        // The tab strip stays above the panes in either orientation: without it
        // there is no sign of activity in the windows that aren't stacked.
        // Shrunk to the strip itself, since the page area below it holds
        // nothing while the logs live in the panes.
        _tabs.Dock = DockStyle.Top;
        _tabs.Height = (_tabs.TabCount > 0 ? _tabs.GetTabRect(0).Bottom : LogicalToDeviceUnits(22))
                       + LogicalToDeviceUnits(4);
        _tabs.Visible = true;
        _splitPanel.Visible = true;
        if (!_splitChannels.Contains(_currentTarget, StringComparer.OrdinalIgnoreCase))
            _currentTarget = _splitChannels[0];
        UpdateSplitHeaderColors();
        // The host has its real size only once the panel above is laid out
        if (layout == StackLayout.Layered) BeginInvoke(LayoutLayeredPanes);

        // Re-tiling resizes every log, which leaves the view wherever the old
        // layout had scrolled to. Put each back at the newest line.
        BeginInvoke(() =>
        {
            foreach (var name in _splitChannels)
                if (_channels.TryGetValue(name, out var c))
                    ScrollToEnd(c.log);
        });
    }

    private static void ScrollToEnd(RichTextBox log)
    {
        log.SelectionStart = log.TextLength;
        log.SelectionLength = 0;
        log.ScrollToCaret();
    }

    // Returns every stacked log to its own TabPage and disposes the panes
    private void TearDownSplitPanes()
    {
        foreach (var name in _splitChannels)
        {
            if (!_channels.TryGetValue(name, out var ch)) continue;
            ch.body.Parent?.Controls.Remove(ch.body);
            ch.log.ContextMenuStrip = _logMenus.GetValueOrDefault(ch.log);
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
        _layerHost?.Dispose();
        _layerHost = null;
    }

    private void ExitSplit()
    {
        TearDownSplitPanes();
        // Everything in the split was visible, so nothing in it is unread
        foreach (var name in _splitChannels)
            _unreadTabs.Remove(name);
        _splitChannels.Clear();
        _splitPanel.Visible = false;
        // Undo the strip-only docking a vertical stack may have left behind
        _tabs.Dock = DockStyle.Fill;
        _tabs.Visible = true;
        _currentTarget = _tabs.SelectedTab?.Text ?? "(server)";
        _tabs.Invalidate();
        if (_channels.TryGetValue(_currentTarget, out var current))
            BeginInvoke(() => ScrollToEnd(current.log));
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

    // Cascades the layered panes: each one offset down and right from the last,
    // all the same size, so every title bar stays visible. Recomputed on resize
    // because the panes are positioned rather than docked.
    private void LayoutLayeredPanes()
    {
        if (_layerHost == null || _splitPanes.Count == 0) return;

        int n = _splitPanes.Count;
        int minW = LogicalToDeviceUnits(120), minH = LogicalToDeviceUnits(80);

        // Cascade by a fixed offset, but never past half the host…
        int step = LogicalToDeviceUnits(28);
        int spread = Math.Min(step * (n - 1), Math.Max(_layerHost.Height / 2, 0));
        step = n > 1 ? spread / (n - 1) : 0;

        int w = Math.Max(_layerHost.Width - step * (n - 1), minW);
        int h = Math.Max(_layerHost.Height - step * (n - 1), minH);

        // …and, in a host too short for that, give up offset rather than push
        // the last pane off the bottom: the minimum size wins over the cascade.
        if (n > 1)
        {
            int fitH = Math.Max(_layerHost.Height - h, 0) / (n - 1);
            int fitW = Math.Max(_layerHost.Width - w, 0) / (n - 1);
            step = Math.Min(step, Math.Min(fitH, fitW));
        }

        for (int i = 0; i < n; i++)
        {
            var pane = _splitPanes[i];
            if (pane.IsDisposed) continue;
            pane.SetBounds(step * i, step * i, w, h);
            // Controls.Add puts each new pane at the back, which would bury the
            // cascade under the first one. Raise them in order so the last sits
            // on top and every header below it stays visible.
            pane.BringToFront();
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
        BuildSplit([.. _splitChannels], _splitLayout);
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
            BuildSplit([.. _splitChannels], _splitLayout);
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
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            var added = dlg.Result;
            ApplyToStoredConnections(list => list.Add(added));
            RefreshConnList(_savedConnections.FindIndex(c => c.Name == added.Name));
        };

        _editConnBtn.Click += (s, e) => EditSelected();

        _deleteConnBtn.Click += (s, e) =>
        {
            int idx = _connList.SelectedIndex;
            if (idx < 0) return;
            var name = _savedConnections[idx].Name;
            // Deleting a connection cannot be undone, so the default button is
            // No: a stray Enter on this dialog must not destroy an entry.
            var confirm = MessageBox.Show(this, $"Delete connection \"{name}\"?", "Confirm Delete",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes) return;

            ApplyToStoredConnections(list =>
            {
                int at = list.FindIndex(c => c.Name == name);
                if (at >= 0) list.RemoveAt(at);
            });
            RefreshConnList();
        };
    }

    // Every write used to dump this process's whole list over the file, so an
    // instance holding a stale list would silently drop whatever it hadn't
    // loaded — another window's additions, or a file restored underneath it.
    // The change is applied to what is on disk *now* instead, and our own list
    // is refreshed from the result.
    private void ApplyToStoredConnections(Action<List<SavedConnection>> change)
    {
        var current = ConnectionStore.LoadOrNull();

        // Unreadable is not the same as empty. Writing over a file we failed to
        // parse is exactly how a bad read becomes lost connections.
        if (current == null)
        {
            MessageBox.Show(this,
                "The saved connections file could not be read, so the change was not saved.\n\n" +
                "Nothing has been overwritten. Close the client and check:\n" +
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                             "IRCClient", "connections.json"),
                "Not Saved", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        change(current);
        WarnIfSaveFailed(ConnectionStore.Save(current));
        _savedConnections = current;
    }

    private void EditSelected()
    {
        int idx = _connList.SelectedIndex;
        if (idx < 0) return;
        var original = _savedConnections[idx];
        using var dlg = new ConnectionEditForm(original);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var edited = dlg.Result;
        ApplyToStoredConnections(list =>
        {
            // Match the entry as it was on disk; append if it isn't there
            int at = list.FindIndex(c => c.Name == original.Name);
            if (at >= 0) list[at] = edited;
            else list.Add(edited);
        });
        RefreshConnList(_savedConnections.FindIndex(c => c.Name == edited.Name));
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
        WireUrlHandling(name, log);
        // Channels get their actions from the nick list; a PM window has none,
        // so the same actions go on its log instead.
        if (IsChannel(name)) BuildNickMenu(name, nicks);
        else if (name != "(server)") AddNickActionsToLogMenu(name, _logMenus[log]);
        // Double-clicking someone in the list opens a private chat with them
        nicks.MouseDoubleClick += (s, e) =>
        {
            int i = nicks.IndexFromPoint(e.Location);
            if (i >= 0) OpenPrivateChat(((string)nicks.Items[i]!).TrimStart('@', '+'));
        };
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

    // http/https/bare-www runs, stopping at whitespace. Trailing sentence
    // punctuation is trimmed afterwards rather than being excluded here, so a
    // link written as "see https://x.com, it's good" keeps its own characters.
    private static readonly Regex UrlPattern = new(
        @"(https?://|www\.)\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Appends text, colouring any URLs in it like links (blue and underlined)
    // while the rest keeps the line's own colour.
    private void AppendWithLinks(RichTextBox log, string text, Color color)
    {
        var plainFont = log.Font;
        var linkFont = new Font(plainFont, plainFont.Style | FontStyle.Underline);

        int pos = 0;
        foreach (Match m in UrlPattern.Matches(text))
        {
            if (m.Index > pos)
            {
                log.SelectionColor = color;
                log.SelectionFont = plainFont;
                log.AppendText(text[pos..m.Index]);
            }
            log.SelectionColor = Color.DeepSkyBlue;
            log.SelectionFont = linkFont;
            log.AppendText(m.Value);
            pos = m.Index + m.Length;
        }
        if (pos < text.Length)
        {
            log.SelectionColor = color;
            log.SelectionFont = plainFont;
            log.AppendText(text[pos..]);
        }
    }

    // The URL the log's context menu was opened over, so "Copy Link" knows what
    // to copy after the click has moved on.
    private string? _menuUrl;

    // Returns the URL under a point in the log, or null. The token is taken as
    // the run of non-whitespace around that character, then stripped of the
    // punctuation people habitually write after a link ("see http://x.com, it's
    // good") and of a wrapping pair of brackets.
    private static string? UrlAt(RichTextBox log, Point p)
    {
        int i = log.GetCharIndexFromPosition(p);
        var text = log.Text;
        if (i < 0 || i >= text.Length || char.IsWhiteSpace(text[i])) return null;

        int start = i, end = i;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        while (end < text.Length - 1 && !char.IsWhiteSpace(text[end + 1])) end++;

        var token = text[start..(end + 1)].Trim('(', ')', '<', '>', '[', ']', '"', '\'');
        token = token.TrimEnd('.', ',', ';', ':', '!', '?');

        if (!token.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !token.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !token.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            return null;

        return token.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? $"http://{token}" : token;
    }

    // Double-click a link to open it, right-click for Copy Link. Wired per log
    // because each channel window builds its own RichTextBox.
    private void WireUrlHandling(string window, RichTextBox log)
    {
        // Keep the highlight on screen once focus returns to the input bar,
        // otherwise a selection made for "Set topic" would grey out and look
        // like it had been lost.
        log.HideSelection = false;

        // Reading or selecting in a log shouldn't leave the caret stranded
        // there. Deferred to MouseUp so a drag-selection completes first.
        log.MouseUp += (s, e) => BeginInvoke(() => _inputBox.Focus());

        // RichTextBox auto-links URLs by default and handles the mouse over
        // them itself, which swallows the double-click and the context menu
        // exactly where a link is. We do our own thing with links, so turn the
        // built-in detection off.
        log.DetectUrls = false;

        // A hand cursor is the only cue that the blue underlined run is live
        log.MouseMove += (s, e) =>
            log.Cursor = UrlAt(log, e.Location) != null ? Cursors.Hand : Cursors.Default;

        log.MouseUp += (s, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            // A click that ends a drag-select is not a click on a link
            if (log.SelectionLength > 0) return;
            if (UrlAt(log, e.Location) is not { } url) return;

            // A link in chat is text a stranger chose, so confirm before handing
            // it to the browser — unless the user has turned that off under
            // File > Options > URL.
            if (!_settings.OpenUrlsWithoutConfirmation)
            {
                var answer = MessageBox.Show(
                    this,
                    $"Open this link in your browser?\n\n{url}",
                    "Open link",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.OK) return;
            }

            try
            {
                // UseShellExecute hands the URL to whatever the user has set as
                // their default browser rather than trying to exec it.
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppendLine(_currentTarget, $"*** Could not open {url}: {ex.Message}", Color.OrangeRed);
            }
        };

        var menu = new ContextMenuStrip();
        var copyLink = new ToolStripMenuItem("Copy Link", null, (s, e) =>
        {
            if (_menuUrl != null) Clipboard.SetText(_menuUrl);
        });
        var copy = new ToolStripMenuItem("Copy", null, (s, e) =>
        {
            if (log.SelectionLength > 0) Clipboard.SetText(log.SelectedText);
        });
        var selectAll = new ToolStripMenuItem("Select All", null, (s, e) => log.SelectAll());
        // Turn highlighted text into the channel's topic
        var setTopic = new ToolStripMenuItem("Set Topic", null, (s, e) =>
        {
            var text = SelectedTopicText(log);
            if (text.Length == 0) return;
            // The highlight is a starting point, not the final wording: open it
            // for editing, and send only on OK.
            if (!PromptText("Set Topic", $"Topic for {window}:", ref text)) return;
            text = text.Trim();
            if (text.Length == 0) return;
            _ = _irc?.SendRawAsync($"TOPIC {window} :{text}");
        });
        var pasteSend = new ToolStripMenuItem("Paste and Send", null, (s, e) => PasteToWindow(window));
        menu.Items.Add(copyLink);
        menu.Items.Add(setTopic);
        menu.Items.Add(pasteSend);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(copy);
        menu.Items.Add(selectAll);
        menu.Opening += (s, e) =>
        {
            _menuUrl = UrlAt(log, log.PointToClient(Cursor.Position));
            copyLink.Visible = _menuUrl != null;
            copyLink.Text = _menuUrl == null ? "Copy Link" : $"Copy Link: {Shorten(_menuUrl)}";
            copy.Enabled = log.SelectionLength > 0;

            // Only in a channel, only with something highlighted, and only
            // while connected — the topic goes to the server.
            var topicText = SelectedTopicText(log);
            setTopic.Visible = IsChannel(window) && topicText.Length > 0 && _irc is { IsConnected: true };
            setTopic.Text = $"Set Topic: {Shorten(topicText)}...";

            // Somewhere to send to, connected, and something to send
            var clip = ClipboardLines();
            pasteSend.Visible = window != "(server)" && clip.Count > 0 && _irc is { IsConnected: true };
            pasteSend.Text = clip.Count > 1
                ? $"Paste and Send {clip.Count} lines to {window}"
                : $"Paste and Send to {window}";
        };
        log.ContextMenuStrip = menu;
        _logMenus[log] = menu;
    }

    private static string Shorten(string url) => url.Length <= 48 ? url : url[..45] + "...";

    // The highlighted text as a topic: a topic is a single line, so newlines
    // become spaces rather than truncating at the first one.
    private static string SelectedTopicText(RichTextBox log) =>
        string.Join(' ', log.SelectedText
                .Replace("\r", "\n")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim()))
            .Trim();

    // Each log's own menu, so split mode can hand it back when it swaps the
    // stacking menu out again.
    private readonly Dictionary<RichTextBox, ContextMenuStrip> _logMenus = [];

    // Right-click menu over a channel's nick list, acting on every selected nick.
    private void BuildNickMenu(string channel, ListBox nicks)
    {
        var menu = new ContextMenuStrip();
        var opItem = new ToolStripMenuItem("Op", null, (s, e) => ModeSelected(channel, 'o', true));
        var deopItem = new ToolStripMenuItem("Deop", null, (s, e) => ModeSelected(channel, 'o', false));
        var voiceItem = new ToolStripMenuItem("Voice", null, (s, e) => ModeSelected(channel, 'v', true));
        var devoiceItem = new ToolStripMenuItem("Devoice", null, (s, e) => ModeSelected(channel, 'v', false));
        var whoisItem = new ToolStripMenuItem("Whois", null, (s, e) => WhoisSelected(channel));
        var pingItem = new ToolStripMenuItem("CTCP Ping", null, (s, e) => CtcpSelected(channel, "PING"));
        var versionItem = new ToolStripMenuItem("CTCP Version", null, (s, e) => CtcpSelected(channel, "VERSION"));
        var timeItem = new ToolStripMenuItem("CTCP Time", null, (s, e) => CtcpSelected(channel, "TIME"));
        var kickItem = new ToolStripMenuItem("Kick", null, (s, e) => KickSelected(channel));
        // Toggles as a group: it offers to stop only when every selected nick
        // is already ignored, so a mixed selection ignores the rest.
        var ignoreItem = new ToolStripMenuItem("Ignore", null, (s, e) =>
        {
            var targets = SelectedNicks(channel);
            if (targets.Count == 0) return;
            var args = string.Join(' ', targets);
            if (targets.All(IsNickIgnored)) RemoveIgnores(args);
            else AddIgnores(args);
        });
        menu.Items.Add(opItem);
        menu.Items.Add(deopItem);
        menu.Items.Add(voiceItem);
        menu.Items.Add(devoiceItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(whoisItem);
        menu.Items.Add(pingItem);
        menu.Items.Add(versionItem);
        menu.Items.Add(timeItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(kickItem);
        menu.Items.Add(ignoreItem);

        // A right-click outside any row has nothing to act on; otherwise label
        // the items with what they are about to affect.
        menu.Opening += (s, e) =>
        {
            int n = nicks.SelectedItems.Count;
            if (n == 0) { e.Cancel = true; return; }
            var suffix = n == 1 ? $" {SelectedNicks(channel)[0]}" : $" {n} nicks";
            opItem.Text = "Op" + suffix;
            deopItem.Text = "Deop" + suffix;
            voiceItem.Text = "Voice" + suffix;
            devoiceItem.Text = "Devoice" + suffix;
            whoisItem.Text = "Whois" + suffix;
            pingItem.Text = "CTCP Ping" + suffix;
            versionItem.Text = "CTCP Version" + suffix;
            timeItem.Text = "CTCP Time" + suffix;
            kickItem.Text = "Kick" + suffix;
            ignoreItem.Text = (SelectedNicks(channel).All(IsNickIgnored) ? "Stop ignoring" : "Ignore") + suffix;
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

    // A private-message window has no nick list to right-click, so its log
    // carries the same actions, all aimed at the one person it is a chat with.
    // Op/Deop/Voice/Devoice/Kick need a channel that a PM doesn't have, so they
    // are bound to the channels we share: a plain item when there is exactly
    // one, a submenu when there are several, disabled when there are none.
    private void AddNickActionsToLogMenu(string nick, ContextMenuStrip menu)
    {
        var whois = new ToolStripMenuItem($"Whois {nick}", null, (s, e) => WhoisNick(nick, nick));
        var ping = new ToolStripMenuItem($"CTCP Ping {nick}", null, (s, e) => CtcpNick(nick, nick, "PING"));
        var version = new ToolStripMenuItem($"CTCP Version {nick}", null, (s, e) => CtcpNick(nick, nick, "VERSION"));
        var time = new ToolStripMenuItem($"CTCP Time {nick}", null, (s, e) => CtcpNick(nick, nick, "TIME"));

        var op = new ToolStripMenuItem("Op");
        var deop = new ToolStripMenuItem("Deop");
        var voice = new ToolStripMenuItem("Voice");
        var devoice = new ToolStripMenuItem("Devoice");
        var kick = new ToolStripMenuItem("Kick");
        // Same list /ignore and Tools > Ignore write to, so the item toggles
        var ignore = new ToolStripMenuItem("Ignore", null, (s, e) =>
        {
            if (IsNickIgnored(nick)) RemoveIgnores(nick);
            else AddIgnores(nick);
        });

        // Inserted at the top, above the log's own Copy items
        var items = new ToolStripItem[]
        {
            op, deop, voice, devoice,
            new ToolStripSeparator(),
            whois, ping, version, time,
            new ToolStripSeparator(),
            kick, ignore,
            new ToolStripSeparator()
        };
        for (int i = 0; i < items.Length; i++)
            menu.Items.Insert(i, items[i]);

        menu.Opening += (s, e) =>
        {
            ignore.Text = IsNickIgnored(nick) ? $"Stop ignoring {nick}" : $"Ignore {nick}";

            var shared = SharedChannels(nick);

            void Bind(ToolStripMenuItem item, string label, Action<string> act)
            {
                item.DropDownItems.Clear();
                item.Click -= OneChannelClick;
                item.Tag = null;

                if (shared.Count == 0)
                {
                    item.Text = $"{label} {nick}";
                    item.Enabled = false;
                    return;
                }
                item.Enabled = true;
                if (shared.Count == 1)
                {
                    item.Text = $"{label} {nick} in {shared[0]}";
                    item.Tag = (act, shared[0]);
                    item.Click += OneChannelClick;
                    return;
                }
                item.Text = $"{label} {nick} in";
                foreach (var c in shared)
                {
                    var channel = c;
                    item.DropDownItems.Add(new ToolStripMenuItem(channel, null, (s2, e2) => act(channel)));
                }
            }

            Bind(op, "Op", c => ModeNick(c, nick, 'o', true));
            Bind(deop, "Deop", c => ModeNick(c, nick, 'o', false));
            Bind(voice, "Voice", c => ModeNick(c, nick, 'v', true));
            Bind(devoice, "Devoice", c => ModeNick(c, nick, 'v', false));
            Bind(kick, "Kick", c => KickNick(c, nick));
        };
    }

    // Click handler for the single-shared-channel case, where the action and
    // its channel are parked in the item's Tag.
    private static void OneChannelClick(object? sender, EventArgs e)
    {
        if (sender is ToolStripMenuItem { Tag: ValueTuple<Action<string>, string> t })
            t.Item1(t.Item2);
    }

    // Single-nick forms of the channel actions, for the PM menu
    private void ModeNick(string channel, string nick, char mode, bool adding) =>
        _ = _irc?.SendRawAsync($"MODE {channel} {(adding ? "+" : "-")}{mode} {nick}");

    private void KickNick(string channel, string nick)
    {
        if (MessageBox.Show(this, $"Kick {nick} from {channel}?", "Confirm kick",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        _ = _irc?.SendRawAsync($"KICK {channel} {nick} :{_irc?.CurrentNick}");
    }

    private void WhoisNick(string window, string nick)
    {
        _replyTarget = window;
        _ = _irc?.SendRawAsync($"WHOIS {nick}");
    }

    private void CtcpNick(string window, string nick, string verb)
    {
        _ctcpReplyWindows[nick] = window;
        var body = verb == "PING" ? $"PING {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}" : verb;
        _ = _irc?.SendRawAsync($"PRIVMSG {nick} :{CtcpMark}{body}{CtcpMark}");
        AppendLine(window, $"*** CTCP {verb} to {nick}", Color.DimGray);
    }

    // Selected nicks with their @ / + status prefix stripped back off
    private List<string> SelectedNicks(string channel) =>
        _channels.TryGetValue(channel, out var ch)
            ? ch.nicks.SelectedItems.Cast<string>()
                  .Select(s => s.TrimStart('@', '+'))
                  .Where(s => s.Length > 0)
                  .ToList()
            : [];

    // Channels we and this nick are both in, and which still have a window
    private List<string> SharedChannels(string nick) =>
        [.. _channelUsers
            .Where(kv => _channels.ContainsKey(kv.Key) && IsChannel(kv.Key) && kv.Value.ContainsKey(nick))
            .Select(kv => kv.Key)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)];

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

    // The CTCP delimiter (SOH). Named rather than escaped inline so the format
    // strings below stay readable.
    private const char CtcpMark = (char)1;

    // Where each outstanding CTCP query was sent from, so its reply (which
    // arrives as a NOTICE, not a numeric) comes back to the same window.
    private readonly Dictionary<string, string> _ctcpReplyWindows = new(StringComparer.OrdinalIgnoreCase);

    // CTCP query to every selected nick. PING carries a millisecond timestamp so
    // the reply can be turned back into a round-trip time.
    private void CtcpSelected(string channel, string verb)
    {
        foreach (var t in SelectedNicks(channel))
        {
            _ctcpReplyWindows[t] = channel;
            var body = verb == "PING"
                ? $"PING {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
                : verb;
            // CTCP is a PRIVMSG whose text is wrapped in the CTCP delimiter
            _ = _irc?.SendRawAsync($"PRIVMSG {t} :{CtcpMark}{body}{CtcpMark}");
            AppendLine(channel, $"*** CTCP {verb} to {t}", Color.DimGray);
        }
    }

    // WHOIS takes a single nick per command on most servers, so ask one at a
    // time. Replies land in the window the menu was opened from, same as a
    // /whois typed there.
    private void WhoisSelected(string channel)
    {
        var targets = SelectedNicks(channel);
        if (targets.Count == 0) return;
        _replyTarget = channel;
        foreach (var t in targets)
            _ = _irc?.SendRawAsync($"WHOIS {t}");
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

    // Opens (or switches to) a private chat window with the given nick — the
    // same tab a PM from them would land in. Talking to yourself is a no-op.
    private void OpenPrivateChat(string nick)
    {
        if (nick.Length == 0 || nick.Equals(_irc?.CurrentNick, StringComparison.OrdinalIgnoreCase))
            return;
        if (!_channels.ContainsKey(nick))
            AddChannelTab(nick);
        // The tab strip is hidden while panes are stacked; the window exists
        // and will be there (with any replies) when the user leaves the split.
        if (!InSplitMode)
            _tabs.SelectedTab = _channels[nick].tab;
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
    // Keeps typing focus on the input line: a printable character pressed while
    // any other control has focus moves focus to the input box and lands there
    // instead of being swallowed (or triggering list type-ahead). Control chars
    // and Ctrl/Alt combinations pass through untouched so shortcuts, Tab
    // completion and menu access still work.
    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        if (!char.IsControl(e.KeyChar)
            && (ModifierKeys & (Keys.Control | Keys.Alt)) == 0
            && !_inputBox.Focused)
        {
            _inputBox.Focus();
            _inputBox.AppendText(e.KeyChar.ToString());
            e.Handled = true;
            return;
        }
        base.OnKeyPress(e);
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

    // --- Tab completion state -------------------------------------------
    // Matches for the word being completed, the position in that list, and
    // where the word starts in the input box. _completing guards the
    // TextChanged handler while we rewrite the box ourselves.
    private readonly List<string> _completeMatches = [];
    private int _completeIndex;
    private int _completeStart;
    private int _completeLength;
    private bool _completing;

    // Replaces the partial nick left of the caret with a matching nick from the
    // channel's user list; pressing Tab again cycles to the next match (Shift+Tab
    // walks backwards). A completion at the very start of the line gets ", "
    // appended, mIRC-style, so "j<Tab>" becomes "j0ker, ".
    private void CompleteNick(bool backwards)
    {
        var text = _inputBox.Text;
        var caret = _inputBox.SelectionStart;

        if (_completeMatches.Count > 0 && caret == _completeStart + _completeLength)
        {
            // Continue the current cycle
            _completeIndex = (_completeIndex + (backwards ? -1 : 1) + _completeMatches.Count) % _completeMatches.Count;
        }
        else
        {
            int start = caret;
            while (start > 0 && text[start - 1] != ' ') start--;
            var partial = text[start..caret];
            if (partial.Length == 0) return;

            _completeMatches.Clear();
            _completeMatches.AddRange(
                UsersOf(_currentTarget).Keys
                    .Where(n => n.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            if (_completeMatches.Count == 0) return;

            _completeIndex = backwards ? _completeMatches.Count - 1 : 0;
            _completeStart = start;
            _completeLength = partial.Length;
        }

        var nick = _completeMatches[_completeIndex];
        var replacement = _completeStart == 0 ? $"{nick}, " : nick;

        _completing = true;
        _inputBox.Text = text[.._completeStart] + replacement + text[(_completeStart + _completeLength)..];
        _completing = false;

        _completeLength = replacement.Length;
        _inputBox.SelectionStart = _completeStart + _completeLength;
        _inputBox.SelectionLength = 0;
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
        AppendWithLinks(log, text + "\n", color ?? Color.LightGray);
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

    // --- Keep alive -------------------------------------------------------
    // A PING to the server on a timer. An idle IRC connection can be dropped
    // by a NAT/router long before either end notices; a line every 60 seconds
    // keeps it live and surfaces a dead link as a failed write.
    private System.Windows.Forms.Timer? _keepAliveTimer;

    private void UpdateKeepAliveTimer()
    {
        bool wanted = _settings.KeepAlive && _irc is { IsConnected: true };

        if (!wanted)
        {
            _keepAliveTimer?.Stop();
            return;
        }

        _keepAliveTimer ??= new System.Windows.Forms.Timer { Interval = 60_000 };
        _keepAliveTimer.Tick -= KeepAliveTick;
        _keepAliveTimer.Tick += KeepAliveTick;
        _keepAliveTimer.Start();
    }

    private void KeepAliveTick(object? sender, EventArgs e)
    {
        if (_irc is not { IsConnected: true }) { _keepAliveTimer?.Stop(); return; }

        // Sent ahead of the flood queue, like PONG: housekeeping shouldn't wait
        // behind a paste, and the reply is a PONG the server sends back.
        var target = _activeServer ?? _irc.CurrentNick ?? "jclient";
        _ = _irc.SendRawAsync($"PING :{target}", urgent: true);
    }

    // Which connection's nicks we are working through, and how far down the
    // list: 0 = the nick itself (already tried), 1 = the alt nick, 2+ = the
    // nick with four random digits.
    private SavedConnection? _nickConnection;
    private int _nickAttempt;

    // The next nick to try after an in-use rejection, or null if there is
    // nothing sensible left to try.
    private string? NextNickCandidate()
    {
        if (_nickConnection is not { } c) return null;
        _nickAttempt++;

        if (_nickAttempt == 1)
        {
            if (!string.IsNullOrWhiteSpace(c.SecondNick)) return c.SecondNick.Trim();
            _nickAttempt++; // no alt nick configured: straight to the numbered one
        }

        var stem = string.IsNullOrWhiteSpace(c.Nick) ? "jclient" : c.Nick.Trim();
        // Keep it inside the 9-character nick limit RFC 2812 §1.2.1 gives as the
        // minimum any server must accept, so the digits are never what's cut.
        if (stem.Length > 5) stem = stem[..5];
        return $"{stem}{Random.Shared.Next(1000, 10000)}";
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
        // Each connection starts at the top of the nick list again
        _nickConnection = c;
        _nickAttempt = 0;
        _irc?.Dispose();
        var conn = new IrcConnection { FloodProtection = _settings.FloodProtection };
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
            _replyTarget = null;
            SetConnectionLost(true);
            _keepAliveTimer?.Stop();
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
            SetConnectionLost(false);
            UpdateKeepAliveTimer();
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
        _replyTarget = null;
        SetConnectionLost(true);
        _keepAliveTimer?.Stop();
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
        // Ignored senders are dropped before anything is displayed or answered
        // — including CTCP, so an ignored user can't provoke an auto-reply.
        if (msg.Command is "PRIVMSG" or "NOTICE" && IsIgnored(msg)) return;

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
                // Anything aimed at us personally rather than at a channel
                var toMe = !target.StartsWith('#') && !target.StartsWith('&');

                // CTCP: text opens with the delimiter. The closing one is optional in
                // the CTCP spec and some clients omit it (notably on PING), so only
                // the opening one is required. Common queries are handled out of band.
                if (text.Length >= 2 && text[0] == CtcpMark)
                {
                    var ctcp = text.Trim('\u0001');
                    var verb = ctcp.Split(' ', 2)[0].ToUpperInvariant();
                    if (toMe)
                        Notify($"CTCP {verb} from {nick}",
                               verb == "ACTION"
                                   ? $"* {nick} {(ctcp.Length > 7 ? ctcp[7..] : "")}"
                                   : $"{nick} sent you a CTCP {verb}");
                    if (verb == "ACTION")
                    {
                        var action = ctcp.Length > 7 ? ctcp[7..] : "";
                        AppendLine(displayTarget, $"* {DisplayNick(displayTarget, nick)} {action}", Color.Plum);
                    }
                    else if (verb == "VERSION")
                    {
                        var reply = string.IsNullOrEmpty(_settings.CustomVersionReply)
                            ? $"jclient IRC by j0ker {VersionString}"
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
                if (toMe) Notify($"Message from {nick}", text);
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
                    _creationShown.Remove(channel);
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

            // RPL_TOPICWHOTIME — "<me> <channel> <setter> <unix seconds>", sent
            // with the topic on join. The setter may be a full nick!user@host.
            case "333":
            {
                var channel = msg.Params.Length > 1 ? msg.Params[1] : "";
                if (channel.Length == 0 || msg.Params.Length < 4) break;
                if (!long.TryParse(msg.Params[3], out var setAt)) break;
                var setter = msg.Params[2].Split('!')[0];
                var when = DateTimeOffset.FromUnixTimeSeconds(setAt).ToLocalTime();
                AppendLine(channel, $"*** Topic set by {setter}, {when:yyyy-MM-dd HH:mm:ss}", Color.DimGray);
                break;
            }

            case "329": // RPL_CREATIONTIME — "<me> <channel> <unix seconds>"
            {
                var channel = msg.Params.Length > 1 ? msg.Params[1] : "";
                if (channel.Length == 0) break;
                if (!long.TryParse(msg.Params.LastOrDefault(), out var epoch)) break;
                // Only worth saying once; it arrives with every mode query.
                if (!_creationShown.Add(channel)) break;
                var created = DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime();
                AppendLine(channel, $"*** Channel created {created:yyyy-MM-dd HH:mm:ss}", Color.DimGray);
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
                // A CTCP reply comes back as a NOTICE opening with the delimiter (the
                // closing one, again, being optional). Decode it rather than printing
                // the control characters raw.
                if (text.Length >= 2 && text[0] == CtcpMark)
                {
                    var body = text.Trim(CtcpMark);
                    var verb = body.Split(' ', 2)[0].ToUpperInvariant();
                    var arg = body.Length > verb.Length ? body[(verb.Length + 1)..] : "";
                    var line = verb switch
                    {
                        // PING echoes the timestamp we sent, so it becomes a round trip
                        "PING" when long.TryParse(arg, out var sent) =>
                            $"*** CTCP PING reply from {nick}: {(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - sent) / 1000.0:0.00}s",
                        "PING" => $"*** CTCP PING reply from {nick}",
                        _ => $"*** CTCP {verb} reply from {nick}: {arg}"
                    };
                    // Back to whichever window asked (right-click menu), falling
                    // back to the active one for replies we didn't initiate.
                    if (_ctcpReplyWindows.Remove(nick, out var askedFrom))
                        AppendLine(askedFrom, line, Color.Yellow);
                    else
                        AppendLine(_currentTarget, line, Color.DimGray);
                    break;
                }
                // Servers that tell you when you've been whois'd (UnrealIRCd's
                // +W, InspIRCd's snomask equivalents) do it with a plain NOTICE,
                // so this is a text match rather than a protocol event — clients
                // get no numeric of their own when someone looks them up.
                if (text.Contains("whois", StringComparison.OrdinalIgnoreCase)
                    && _irc?.CurrentNick is { Length: > 0 } me
                    && text.Contains(me, StringComparison.OrdinalIgnoreCase))
                    Notify("Whois", text);

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

            // ERR_NICKNAMEINUSE — work down the fallbacks rather than sitting
            // unregistered: the alt nick from the connection, then the primary
            // with four random digits, renumbering on each further clash.
            case "433":
            {
                var taken = msg.Params.Length > 1 ? msg.Params[1] : msg.Params.LastOrDefault() ?? "";
                AppendLine("(server)", $"*** Nick already in use: {taken}", Color.Orange);

                var next = NextNickCandidate();
                if (next != null)
                {
                    AppendLine("(server)", $"*** Trying {next}", Color.Cyan);
                    _ = _irc?.SendRawAsync($"NICK {next}", urgent: true);
                }
                break;
            }

            // ERR_BADCHANNELKEY / ERR_INVITEONLYCHAN / ERR_BANNEDFROMCHAN
            case "475": case "473": case "474":
                AppendLine("(server)", $"*** Cannot join channel: {string.Join(" ", msg.Params.Skip(1))}", Color.Orange);
                break;

            default:
                // Numeric replies go to the window whose command asked for them
                // (yellow), or to the server tab when nothing is outstanding.
                if (int.TryParse(msg.Command, out _))
                {
                    var line = $"[{msg.Command}] {string.Join(" ", msg.Params)}";
                    if (_replyTarget != null)
                    {
                        AppendLine(_replyTarget, line, Color.Yellow);
                        if (ReplyEndNumerics.Contains(msg.Command)) _replyTarget = null;
                    }
                    else AppendLine("(server)", line, Color.DimGray);
                }
                break;
        }
    }

    private async void OnSend(object? s, EventArgs e)
    {
        // The last word has no trailing space to trigger a correction, so give
        // it one before the line leaves.
        if (!_inputBox.Text.StartsWith('/')) AutoCorrectWordBeforeCaret();

        var text = _inputBox.Text.Trim();
        _inputBox.Clear();

        // Record in command history (skip consecutive duplicates) and reset the
        // Up/Down browse position to "past the newest entry". Done before the
        // empty-line check: pressing Enter on an empty box used to leave the
        // browse position stale, so a later Down would wipe what was typed.
        if (text.Length > 0 && (_inputHistory.Count == 0 || _inputHistory[^1] != text))
            _inputHistory.Add(text);
        _historyIndex = _inputHistory.Count;
        _historyDraft = "";
        _browsingHistory = false;

        if (string.IsNullOrEmpty(text)) return;

        await SubmitLine(text);
    }

    // One line on its way out, whether it was typed or pasted: a command runs,
    // anything else goes to the current window as a message.
    private async Task SubmitLine(string text)
    {
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

    // How many pasted lines go out without asking. Past this it's worth a
    // confirmation: a stray paste into a channel is public and irreversible.
    private const int PasteConfirmThreshold = 3;

    // The input box is single-line, so a pasted block would otherwise arrive as
    // one run-together line. Split it and send a line at a time instead; flood
    // protection (if on) paces them.
    // --- Spell checking on the input line --------------------------------
    // Marking is done by re-colouring the box's text, which itself raises
    // TextChanged; this guards against re-entering. The check runs on a short
    // timer so it happens once the typing pauses rather than per keystroke.
    private bool _spellMarking;
    private System.Windows.Forms.Timer? _spellTimer;
    // Words the menu's Ignore was used on, kept for the session
    private readonly HashSet<string> _spellIgnored = new(StringComparer.OrdinalIgnoreCase);

    private void WireSpellCheck()
    {
        if (!SpellCheck.Available) return;

        _spellTimer = new System.Windows.Forms.Timer { Interval = 400 };
        _spellTimer.Tick += (s, e) => { _spellTimer!.Stop(); MarkMisspellings(); };

        // A word is finished by a space or by punctuation; correct it then, so
        // the change is visible while typing rather than at send time.
        _inputBox.KeyPress += (s, e) =>
        {
            if (e.KeyChar != ' ' && !char.IsPunctuation(e.KeyChar)) return;
            BeginInvoke(AutoCorrectWordBeforeCaret);
        };

        // Typing over a recalled command ends the browse: what is in the box is
        // now the user's line, not a history entry to arrow away from.
        _inputBox.KeyPress += (s, e) =>
        {
            if (!char.IsControl(e.KeyChar)) _browsingHistory = false;
        };
        _inputBox.TextChanged += (s, e) =>
        {
            if (_spellMarking) return;
            NoteCorrectionUndone();
            _spellTimer!.Stop();
            _spellTimer.Start();
        };
    }

    // Commands are not prose: "/join #chan" shouldn't come back covered in red.
    private static bool IsCheckable(string text) => !text.StartsWith('/');

    private void MarkMisspellings()
    {
        if (!SpellCheck.Available || _spellMarking) return;

        var text = _inputBox.Text;
        var errors = IsCheckable(text)
            ? SpellCheck.Check(text).Where(m => !_spellIgnored.Contains(text.Substring(m.Start, m.Length))).ToList()
            : [];

        _spellMarking = true;
        int caret = _inputBox.SelectionStart, selLen = _inputBox.SelectionLength;
        try
        {
            _inputBox.SelectAll();
            _inputBox.SelectionColor = _inputBox.ForeColor;
            _inputBox.SelectionFont = _inputBox.Font;

            foreach (var m in errors)
            {
                if (m.Start + m.Length > text.Length) continue;
                _inputBox.Select(m.Start, m.Length);
                _inputBox.SelectionColor = Color.OrangeRed;
                _inputBox.SelectionFont = new Font(_inputBox.Font, FontStyle.Underline);
            }
        }
        finally
        {
            _inputBox.Select(caret, selLen);
            // Anything typed next is normal text again, not a continuation of
            // whatever run the caret happens to sit at the end of.
            if (selLen == 0)
            {
                _inputBox.SelectionColor = _inputBox.ForeColor;
                _inputBox.SelectionFont = _inputBox.Font;
            }
            _spellMarking = false;
        }
    }

    // --- Autocorrect ------------------------------------------------------
    // A correction gets one attempt: if it is undone — the word typed back the
    // way it was — that spelling is left alone from then on, however the user
    // meant it. Words nobody has overruled keep being corrected.
    private readonly HashSet<string> _autoCorrectOff = new(StringComparer.OrdinalIgnoreCase);

    // The correction just made, watched for being undone
    private (string Original, string Fixed, int Start)? _lastCorrection;

    // Called as the text changes: if the word we corrected has been put back
    // the way it was, that is the user overruling us, and it stands.
    private void NoteCorrectionUndone()
    {
        if (_lastCorrection is not { } last) return;

        var text = _inputBox.Text;
        if (last.Start + last.Fixed.Length <= text.Length
            && text.Substring(last.Start, last.Fixed.Length) == last.Fixed)
            return; // our correction is still standing

        // It was changed — back to the original or to something else entirely.
        // Either way the user has decided how that word is spelt.
        _autoCorrectOff.Add(last.Original);
        _lastCorrection = null;
    }

    // The fix for a word, or null to leave it alone. Only two kinds are made:
    // the standalone "i", and a misspelling whose correction is the same
    // letters with an apostrophe — im to I'm, dont to don't, youre to you're.
    // Anything needing a different word is left to the suggestions menu.
    private string? AutoCorrection(string word)
    {
        if (word.Length == 0 || _autoCorrectOff.Contains(word)) return null;

        if (word == "i") return "I";
        // Windows doesn't flag these: they are real words, so a lone
        // apostrophe rule can't reach them and nothing is guessed for them.
        if (!word.Contains('\'') && SpellCheck.Check(word).Count > 0)
        {
            static string Bare(string s) => s.Replace("'", "").Replace("’", "").ToLowerInvariant();
            foreach (var suggestion in SpellCheck.Suggest(word))
                if (suggestion.Contains('\'') && Bare(suggestion) == Bare(word))
                    return suggestion;
        }
        return null;
    }

    // Corrects the word immediately before the caret, as it is completed.
    private void AutoCorrectWordBeforeCaret()
    {
        if (!SpellCheck.Available || _spellMarking) return;

        var text = _inputBox.Text;
        int caret = _inputBox.SelectionStart;
        if (caret > text.Length) return;

        // Walk back over whatever ended the word, then over the word itself
        int end = caret;
        while (end > 0 && !char.IsLetter(text[end - 1]) && text[end - 1] != '\'') end--;
        int start = end;
        while (start > 0 && (char.IsLetter(text[start - 1]) || text[start - 1] == '\'')) start--;
        if (end <= start) return;

        var word = text[start..end];
        if (AutoCorrection(word) is not { } fixedWord || fixedWord == word) return;

        _spellMarking = true;
        _inputBox.Select(start, end - start);
        _inputBox.SelectedText = fixedWord;
        _inputBox.SelectionStart = caret + (fixedWord.Length - word.Length);
        _inputBox.SelectionLength = 0;
        _spellMarking = false;
        _lastCorrection = (word, fixedWord, start);
        MarkMisspellings();
    }

    // The misspelled word under the mouse, as (word, start, length)
    private (string Word, int Start, int Length)? MisspellingAtCursor()
    {
        if (!SpellCheck.Available) return null;
        var text = _inputBox.Text;
        if (!IsCheckable(text)) return null;

        int index = _inputBox.GetCharIndexFromPosition(_inputBox.PointToClient(Cursor.Position));
        foreach (var m in SpellCheck.Check(text))
        {
            if (index < m.Start || index > m.Start + m.Length) continue;
            var word = text.Substring(m.Start, m.Length);
            if (_spellIgnored.Contains(word)) continue;
            return (word, m.Start, m.Length);
        }
        return null;
    }

    // Puts suggestions for the word under the cursor at the top of the input
    // box's menu, above Cut/Copy/Paste, and clears them again next time.
    private void AddSpellingSuggestions(ContextMenuStrip menu)
    {
        foreach (var item in menu.Items.Cast<ToolStripItem>().Where(i => i.Tag as string == "spell").ToList())
            menu.Items.Remove(item);

        var hit = MisspellingAtCursor();
        if (hit == null) return;
        var (word, start, length) = hit.Value;

        int at = 0;
        void Insert(ToolStripItem item)
        {
            item.Tag = "spell";
            menu.Items.Insert(at++, item);
        }

        foreach (var suggestion in SpellCheck.Suggest(word).Take(5))
        {
            var replacement = suggestion;
            Insert(new ToolStripMenuItem(replacement, null, (s, e) =>
            {
                _spellMarking = true;
                _inputBox.Select(start, length);
                _inputBox.SelectedText = replacement;
                _inputBox.SelectionStart = start + replacement.Length;
                _inputBox.SelectionLength = 0;
                _spellMarking = false;
                MarkMisspellings();
            }) { Font = new Font(menu.Font, FontStyle.Bold) });
        }

        Insert(new ToolStripMenuItem($"Add \"{word}\" to Dictionary", null, (s, e) =>
        {
            SpellCheck.Add(word);
            MarkMisspellings();
        }));
        Insert(new ToolStripMenuItem($"Ignore \"{word}\"", null, (s, e) =>
        {
            _spellIgnored.Add(word);
            MarkMisspellings();
        }));
        Insert(new ToolStripSeparator());
    }

    // Clipboard text as sendable lines: blank lines dropped, line endings
    // normalised. Empty when the clipboard is busy or holds something else.
    private static List<string> ClipboardLines()
    {
        string clip;
        try { clip = Clipboard.GetText(); }
        catch { return []; }

        if (string.IsNullOrEmpty(clip)) return [];
        return [.. clip.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => l.Length > 0)];
    }

    private async void PasteIntoInput()
    {
        var lines = ClipboardLines();
        if (lines.Count == 0) return;

        // Single line: behave like an ordinary paste — into the box, not sent
        if (lines.Count <= 1)
        {
            var one = lines.Count == 1 ? lines[0] : "";
            var at = _inputBox.SelectionStart;
            _inputBox.Text = _inputBox.Text.Remove(at, _inputBox.SelectionLength).Insert(at, one);
            _inputBox.SelectionStart = at + one.Length;
            return;
        }

        if (_irc == null || _currentTarget is "(server)" or "") return;

        if (lines.Count > PasteConfirmThreshold)
        {
            var preview = string.Join("\n", lines.Take(3));
            if (lines.Count > 3) preview += $"\n... and {lines.Count - 3} more";
            var answer = MessageBox.Show(
                this,
                $"Send {lines.Count} lines to {_currentTarget}?\n\n{preview}",
                "Paste",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.OK) return;
        }

        foreach (var line in lines)
        {
            if (_irc is not { IsConnected: true }) break;
            await SubmitLine(line);
        }
    }

    // Right-click > Paste in a channel or PM window: send the clipboard there
    // as chat. Lines go out verbatim — a pasted "/quit" is said, not run —
    // since this pastes text into a conversation rather than typing commands.
    private async void PasteToWindow(string window)
    {
        var lines = ClipboardLines();
        if (lines.Count == 0 || _irc is not { IsConnected: true }) return;

        if (lines.Count > PasteConfirmThreshold)
        {
            var preview = string.Join("\n", lines.Take(3));
            if (lines.Count > 3) preview += $"\n... and {lines.Count - 3} more";
            var answer = MessageBox.Show(
                this,
                $"Send {lines.Count} lines to {window}?\n\n{preview}",
                "Paste",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.OK) return;
        }

        foreach (var line in lines)
        {
            if (_irc is not { IsConnected: true }) break;
            await _irc.PrivMsgAsync(window, line);
            AppendLine(window, $"<{DisplayNick(window, _irc.CurrentNick ?? "")}> {line}", Color.LightYellow);
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

        // Remember where a query was issued so its replies come back here.
        if (QueryCommands.Contains(verb))
            _replyTarget = _currentTarget;

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
            // /ignore              list the masks
            // /ignore <mask>...    add each
            // /ignore -r <mask>... remove each (also /unignore)
            case "IGNORE":
                if (rest.StartsWith("-r", StringComparison.OrdinalIgnoreCase))
                    RemoveIgnores(rest[2..]);
                else
                    AddIgnores(rest);
                break;
            case "UNIGNORE":
                RemoveIgnores(rest);
                break;
            // Wipes the active window's scrollback only; nothing is sent and no
            // other window is touched.
            case "CLEAR":
                if (_channels.TryGetValue(_currentTarget, out var clearTarget))
                    clearTarget.log.Clear();
                break;
            default:
                // Unrecognised verbs are passed straight to the server, so their
                // replies belong in this window too.
                _replyTarget = _currentTarget;
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

    // Notification-area icon, created on demand the first time the window is
    // hidden. Double-clicking it (or its Open item) brings the window back;
    // Exit quits for real.
    private NotifyIcon? _tray;

    // The icon stays visible for the life of the app, not just while hidden,
    // so it can raise balloon notifications at any time.
    private NotifyIcon Tray()
    {
        if (_tray == null)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open", null, (s, e) => RestoreFromTray());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => { _exitFromTray = true; Close(); });
            _tray = new NotifyIcon
            {
                Icon = AppIcon.Get(),
                Text = "jclient irc",
                ContextMenuStrip = menu,
                Visible = true
            };
            _tray.DoubleClick += (s, e) => RestoreFromTray();
            _tray.BalloonTipClicked += (s, e) => RestoreFromTray();
        }
        return _tray;
    }

    private void HideToTray()
    {
        Tray();
        Hide();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    // The banner currently on screen, if any; a second notification reuses it
    // rather than stacking windows up the corner of the screen.
    private Form? _banner;
    private System.Windows.Forms.Timer? _bannerTimer;

    // Three-second banner in the bottom-right corner, raised only when the
    // window isn't the one being looked at — hidden to tray, minimised, or
    // simply not focused. Anything happening in plain sight stays silent.
    //
    // Drawn as our own topmost window rather than NotifyIcon.ShowBalloonTip:
    // balloons are delivered as toasts on Windows 10/11 and get suppressed
    // outright by focus assist or per-app notification settings.
    private void Notify(string title, string text)
    {
        if (Visible && WindowState != FormWindowState.Minimized && ContainsFocus) return;

        if (_banner == null)
        {
            _banner = new BannerForm
            {
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                TopMost = true,
                StartPosition = FormStartPosition.Manual,
                BackColor = Color.FromArgb(28, 28, 40),
                Size = LogicalToDeviceUnits(new Size(320, 80))
            };
            var titleLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = LogicalToDeviceUnits(24),
                ForeColor = Color.Yellow,
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                Padding = new Padding(LogicalToDeviceUnits(8), LogicalToDeviceUnits(4), 0, 0),
                AutoEllipsis = true
            };
            var bodyLabel = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Color.White,
                Padding = new Padding(LogicalToDeviceUnits(8), 0, LogicalToDeviceUnits(8), LogicalToDeviceUnits(4)),
                AutoEllipsis = true
            };
            _banner.Controls.Add(bodyLabel);
            _banner.Controls.Add(titleLabel);
            _banner.Tag = (titleLabel, bodyLabel);
            // Clicking anywhere on the banner brings the client back up
            void Restore(object? s, EventArgs e) { HideBanner(); RestoreFromTray(); }
            _banner.Click += Restore;
            titleLabel.Click += Restore;
            bodyLabel.Click += Restore;
        }

        var (t, b) = ((Label, Label))_banner.Tag!;
        t.Text = title;
        b.Text = text;

        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 768);
        _banner.Location = new Point(
            area.Right - _banner.Width - LogicalToDeviceUnits(12),
            area.Bottom - _banner.Height - LogicalToDeviceUnits(12));

        // BannerForm.ShowWithoutActivation keeps focus in whatever the user is
        // typing in; the banner only appears, it never steals the keyboard.
        _banner.Show();

        _bannerTimer ??= new System.Windows.Forms.Timer();
        _bannerTimer.Stop();
        _bannerTimer.Interval = 3000;
        _bannerTimer.Tick -= BannerElapsed;
        _bannerTimer.Tick += BannerElapsed;
        _bannerTimer.Start();
    }

    // A popup that appears without taking focus away from whatever the user
    // is doing — the whole point of a passive notification.
    private sealed class BannerForm : Form
    {
        protected override bool ShowWithoutActivation => true;
    }

    private void BannerElapsed(object? sender, EventArgs e) => HideBanner();

    private void HideBanner()
    {
        _bannerTimer?.Stop();
        _banner?.Hide();
    }

    // Set by the tray menu's Exit so the close it triggers isn't swallowed
    // by the minimize-to-tray branch below.
    private bool _exitFromTray;

    // --- Window placement -------------------------------------------------
    // Saved as the window moves rather than only on exit, so a crash or a kill
    // still leaves the last position behind. The write is debounced: dragging a
    // window raises a stream of these.
    private System.Windows.Forms.Timer? _placementTimer;
    private bool _placementRestored;

    private void RestoreWindowPlacement()
    {
        if (_settings.WindowWidth <= 0 || _settings.WindowHeight <= 0) return;

        var saved = new Rectangle(_settings.WindowX, _settings.WindowY,
                                  _settings.WindowWidth, _settings.WindowHeight);

        // A monitor that has since been unplugged (or rearranged) would put the
        // window somewhere unreachable, so only honour a position still on a
        // screen. Size is kept either way.
        bool onScreen = Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(saved));
        StartPosition = onScreen ? FormStartPosition.Manual : FormStartPosition.WindowsDefaultLocation;
        if (onScreen) Location = saved.Location;
        Size = new Size(Math.Max(saved.Width, MinimumSize.Width),
                        Math.Max(saved.Height, MinimumSize.Height));

        if (_settings.WindowMaximized) WindowState = FormWindowState.Maximized;
        _placementRestored = true;
    }

    private void SchedulePlacementSave()
    {
        // Ignore the layout churn while the form is still being built
        if (!IsHandleCreated || (!_placementRestored && !Visible)) return;

        _placementTimer ??= new System.Windows.Forms.Timer { Interval = 800 };
        _placementTimer.Tick -= PlacementTick;
        _placementTimer.Tick += PlacementTick;
        _placementTimer.Stop();
        _placementTimer.Start();
    }

    private void PlacementTick(object? sender, EventArgs e)
    {
        _placementTimer?.Stop();
        SaveWindowPlacement();
    }

    private void SaveWindowPlacement()
    {
        // Minimised has no useful geometry, and RestoreBounds carries the
        // pre-maximise rectangle — which is what should come back on restore.
        if (WindowState == FormWindowState.Minimized) return;

        var bounds = WindowState == FormWindowState.Maximized ? RestoreBounds : Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        _settings.WindowX = bounds.X;
        _settings.WindowY = bounds.Y;
        _settings.WindowWidth = bounds.Width;
        _settings.WindowHeight = bounds.Height;
        _settings.WindowMaximized = WindowState == FormWindowState.Maximized;
        SettingsStore.Save(_settings);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        SchedulePlacementSave();
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        SchedulePlacementSave();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveWindowPlacement();

        // "Keep running when closed": the X button hides the window and leaves
        // the connection up. Windows shutting down or the tray's own Exit still
        // close for real.
        if (_settings.MinimizeToTrayOnClose && !_exitFromTray
            && e.CloseReason is CloseReason.UserClosing or CloseReason.None)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

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
        // Without this the icon lingers in the notification area until the
        // user hovers over it.
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _bannerTimer?.Dispose();
        _banner?.Dispose();
        base.OnFormClosed(e);
    }
}
