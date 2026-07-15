using System.Drawing;
using System.Windows.Forms;

namespace IRCClient;

public partial class MainForm : Form
{
    private IrcConnection? _irc;
    private bool _connecting;

    // Channel tabs: channel name -> (TabPage, header label, RichTextBox)
    private readonly Dictionary<string, (TabPage tab, Label header, RichTextBox log)> _channels = new(StringComparer.OrdinalIgnoreCase);
    private string _currentTarget = "";

    // Channel topics (from 332 on join and TOPIC changes) and the server we're
    // connected to, both shown in every window's header line.
    private readonly Dictionary<string, string> _topics = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeServer;

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
        connectOptions.DropDownItems.Add(connectOnStartupItem);
        connectOptions.DropDownItems.Add(reconnectOnDisconnectItem);
        optionsItem.DropDownItems.Add(connectOptions);
        var logOptions = new ToolStripMenuItem("Log");
        string LogDirDisplay() => string.IsNullOrEmpty(_settings.LogDirectory)
            ? "Logging: off"
            : $"Logging to: {_settings.LogDirectory}";
        var currentLogDirItem = new ToolStripMenuItem(LogDirDisplay()) { Enabled = false };
        var setLogDirItem = new ToolStripMenuItem("Set Log Directory...");
        setLogDirItem.Click += (s, e) =>
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Choose the catch-all folder where all chat and server logs are written",
                UseDescriptionForTitle = true,
                InitialDirectory = _settings.LogDirectory
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _settings.LogDirectory = dlg.SelectedPath;
                SettingsStore.Save(_settings);
                currentLogDirItem.Text = LogDirDisplay();
            }
        };
        var disableLogItem = new ToolStripMenuItem("Disable Logging");
        disableLogItem.Click += (s, e) =>
        {
            _settings.LogDirectory = "";
            SettingsStore.Save(_settings);
            currentLogDirItem.Text = LogDirDisplay();
        };
        logOptions.DropDownItems.Add(setLogDirItem);
        logOptions.DropDownItems.Add(disableLogItem);
        logOptions.DropDownItems.Add(new ToolStripSeparator());
        logOptions.DropDownItems.Add(currentLogDirItem);
        optionsItem.DropDownItems.Add(logOptions);
        var aboutOptions = new ToolStripMenuItem("About");
        // Version comes from <Version> in the csproj; strip any "+commit" suffix
        // the SDK appends to the informational version.
        var version = Application.ProductVersion;
        int plusIdx = version.IndexOf('+');
        if (plusIdx >= 0) version = version[..plusIdx];
        aboutOptions.DropDownItems.Add(new ToolStripMenuItem("jclient irc for Windows by j0ker") { Enabled = false });
        aboutOptions.DropDownItems.Add(new ToolStripMenuItem($"version {version}") { Enabled = false });
        optionsItem.DropDownItems.Add(aboutOptions);
        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (s, e) => Close();
        fileMenu.DropDownItems.Add(disconnectItem);
        fileMenu.DropDownItems.Add(optionsItem);
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(exitItem);
        _menu.Items.Add(fileMenu);
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
        };

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
        var closeItem = new ToolStripMenuItem("Close");
        closeItem.Click += async (s, e) =>
        {
            if (_rightClickedTab != null)
                await CloseTab(_rightClickedTab.Text);
        };
        tabMenu.Items.Add(stackHItem);
        tabMenu.Items.Add(stackVItem);
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
            log.Parent?.Controls.Remove(log);
            log.ContextMenuStrip = _splitMenu;
            pane.Controls.Add(log);
            pane.Controls.Add(header);
            _splitPanes.Add(pane);
            _splitHeaders[name] = header;
            _splitPanel.Controls.Add(pane, horizontal ? i : 0, horizontal ? 0 : i);
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
            ch.log.Parent?.Controls.Remove(ch.log);
            ch.log.ContextMenuStrip = null;
            ch.tab.Controls.Add(ch.log);
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
            bool active = name.Equals(_currentTarget, StringComparison.OrdinalIgnoreCase);
            header.BackColor = active ? Color.FromArgb(60, 90, 150) : Color.FromArgb(45, 45, 60);
            header.ForeColor = active ? Color.White : Color.LightGray;
        }
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

    private void BuildLibraryPanel()
    {
        var header = new Label
        {
            Text = "Connections",
            Dock = DockStyle.Top,
            Height = LogicalToDeviceUnits(22),
            Font = new Font(Font, FontStyle.Bold)
        };

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
            Font = new Font("Consolas", 9.5f),
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
        var tab = new TabPage(name);
        tab.Controls.Add(log);
        tab.Controls.Add(header);
        _tabs.TabPages.Add(tab);
        _channels[name] = (tab, header, log);
    }

    private string ComposeHeader(string name)
    {
        var gap = new string(' ', 5);
        var text = name;
        if (_irc is { IsConnected: true, CurrentNick: not null } && _activeServer != null)
            text += $"{gap}{_irc.CurrentNick} @ {_activeServer}";
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
    private void WriteToLogFile(string target, string text)
    {
        var dir = _settings.LogDirectory;
        if (string.IsNullOrEmpty(dir)) return;
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
            if (_irc != conn) return;
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

        // Skip if the user connected somewhere else or clicked Disconnect while
        // we were waiting — _irc no longer points at the connection that died.
        if (_irc != failedConn) return;

        await ConnectAsync(c);
    }

    private async void OnDisconnect(object? s, EventArgs e)
    {
        if (_irc == null) return;
        try { await _irc.QuitAsync("Leaving"); }
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
                // PM to us — show in their nick tab
                var displayTarget = target.StartsWith('#') || target.StartsWith('&') ? target : nick;
                AppendLine(displayTarget, $"<{nick}> {text}", Color.White);
                break;
            }

            case "JOIN":
            {
                var channel = msg.Params[0];
                var nick = msg.PrefixNick ?? "";
                if (!_channels.ContainsKey(channel))
                    AddChannelTab(channel);
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
                // If it's us parting and the tab is still open (e.g. via /part command), close it
                if (nick.Equals(_irc?.CurrentNick, StringComparison.OrdinalIgnoreCase)
                    && _channels.TryGetValue(channel, out var ch))
                {
                    _channels.Remove(channel);
                    _unreadTabs.Remove(channel);
                    _ctrlSelectedTabs.Remove(channel);
                    _topics.Remove(channel);
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
                foreach (var kv in _channels)
                    AppendLine(kv.Key, $"*** {nick} quit ({reason})", Color.DimGray);
                break;
            }

            case "NICK":
            {
                var oldNick = msg.PrefixNick ?? "";
                var newNick = msg.Params[0];
                foreach (var kv in _channels)
                    AppendLine(kv.Key, $"*** {oldNick} is now {newNick}", Color.Plum);
                UpdateAllHeaders(); // IrcConnection tracks our own nick changes
                break;
            }

            case "353": // RPL_NAMREPLY
            {
                var channel = msg.Params.Length > 2 ? msg.Params[2] : "";
                var names = msg.Params.LastOrDefault() ?? "";
                AppendLine(channel, $"*** Users: {names}", Color.DimGray);
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
            AppendLine(_currentTarget, $"<{_irc.CurrentNick}> {text}", Color.LightYellow);
        }
    }

    private async Task HandleCommand(string cmd)
    {
        if (_irc == null) return;
        var parts = cmd.Split(' ', 2);
        var verb = parts[0].ToUpperInvariant();
        var rest = parts.Length > 1 ? parts[1] : "";

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
                    AppendLine(args[0], $"<{_irc.CurrentNick}> {args[1]}", Color.LightYellow);
                }
                break;
            }
            case "NICK":
                await _irc.SendRawAsync($"NICK {rest}");
                break;
            case "QUIT":
                await _irc.QuitAsync(rest.Length > 0 ? rest : "Goodbye");
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
                    var action = $"\x01ACTION {rest}\x01";
                    await _irc.PrivMsgAsync(_currentTarget, action);
                    AppendLine(_currentTarget, $"* {_irc.CurrentNick} {rest}", Color.Plum);
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
        if (!_settings.ConnectOnStartup || _savedConnections.Count == 0) return;

        var c = _savedConnections.FirstOrDefault(x => x.Name == _settings.LastConnectionName)
                ?? _savedConnections[0];
        _connList.SelectedIndex = _savedConnections.IndexOf(c);
        ConnectToSelected();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _irc?.Dispose();
        base.OnFormClosed(e);
    }
}
