using System.Drawing;
using System.Windows.Forms;

namespace IRCClient;

public partial class MainForm : Form
{
    private IrcConnection? _irc;
    private bool _connecting;

    // Channel tabs: channel name -> (TabPage, RichTextBox)
    private readonly Dictionary<string, (TabPage tab, RichTextBox log)> _channels = new(StringComparer.OrdinalIgnoreCase);
    private string _currentTarget = "";

    // Tabs that received messages while not the active tab; drawn highlighted
    // until the user opens them.
    private readonly HashSet<string> _unreadTabs = new(StringComparer.OrdinalIgnoreCase);

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
        _mainLayout.Controls.Add(_tabs, 0, 0);
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

        // Owner-draw the tab headers so tabs with unread activity can be
        // highlighted; the default renderer has no per-tab text color.
        _tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        _tabs.DrawItem += (s, e) =>
        {
            if (e.Index < 0 || e.Index >= _tabs.TabCount) return;
            var tab = _tabs.TabPages[e.Index];
            e.DrawBackground();
            var color = _unreadTabs.Contains(tab.Text) ? Color.DarkOrange : _tabs.ForeColor;
            TextRenderer.DrawText(e.Graphics, tab.Text, _tabs.Font, e.Bounds, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };

        // Middle-click closes a tab
        _tabs.MouseDown += (s, e) =>
        {
            if (e.Button == MouseButtons.Middle)
                CloseTabAt(e.Location);
        };

        // Right-click shows context menu on the specific tab that was clicked
        TabPage? _rightClickedTab = null;
        var tabMenu = new ContextMenuStrip();
        var closeItem = new ToolStripMenuItem("Close");
        closeItem.Click += async (s, e) =>
        {
            if (_rightClickedTab != null)
                await CloseTab(_rightClickedTab.Text);
        };
        tabMenu.Items.Add(closeItem);
        _tabs.MouseDown += (s, e) =>
        {
            if (e.Button == MouseButtons.Right)
            {
                var tab = TabPageAt(e.Location);
                if (tab != null && tab.Text != "(server)")
                {
                    _rightClickedTab = tab;
                    tabMenu.Show(_tabs, e.Location);
                }
            }
        };

        // Ctrl+A selects all text in the input box
        _inputBox.KeyDown += (s, e) =>
        {
            if (e.Control && e.KeyCode == Keys.A)
            {
                _inputBox.SelectAll();
                e.SuppressKeyPress = true;
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
        _tabs.TabPages.Remove(ch.tab);

        if (_currentTarget.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            _currentTarget = "(server)";
            if (_channels.TryGetValue("(server)", out var srv))
                _tabs.SelectedTab = srv.tab;
        }
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

    private RichTextBox AddChannelTab(string name)
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
        var tab = new TabPage(name);
        tab.Controls.Add(log);
        _tabs.TabPages.Add(tab);
        _channels[name] = (tab, log);
        return log;
    }

    private void AppendLine(string target, string text, Color? color = null)
    {
        if (!_channels.TryGetValue(target, out var ch))
            ch = (default!, AddChannelTab(target));

        var log = ch.log;
        var ts = DateTime.Now.ToString("HH:mm");
        log.SelectionStart = log.TextLength;
        log.SelectionLength = 0;
        log.SelectionColor = Color.Gray;
        log.AppendText($"[{ts}] ");
        log.SelectionColor = color ?? Color.LightGray;
        log.AppendText(text + "\n");
        log.ScrollToCaret();

        // Highlight the tab if this message landed somewhere the user isn't looking
        if (!target.Equals(_currentTarget, StringComparison.OrdinalIgnoreCase) && _unreadTabs.Add(target))
            _tabs.Invalidate();
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
                    _tabs.TabPages.Remove(ch.tab);
                    if (_currentTarget.Equals(channel, StringComparison.OrdinalIgnoreCase))
                    {
                        _currentTarget = "(server)";
                        if (_channels.TryGetValue("(server)", out var srv))
                            _tabs.SelectedTab = srv.tab;
                    }
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
                break;
            }

            case "353": // RPL_NAMREPLY
            {
                var channel = msg.Params.Length > 2 ? msg.Params[2] : "";
                var names = msg.Params.LastOrDefault() ?? "";
                AppendLine(channel, $"*** Users: {names}", Color.DimGray);
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
        if (string.IsNullOrEmpty(text) || _irc == null) return;

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
