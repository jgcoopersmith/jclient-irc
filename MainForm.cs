using System.Drawing;
using System.Windows.Forms;

namespace IRCClient;

public partial class MainForm : Form
{
    private IrcConnection? _irc;

    // Channel tabs: channel name -> (TabPage, RichTextBox)
    private readonly Dictionary<string, (TabPage tab, RichTextBox log)> _channels = new(StringComparer.OrdinalIgnoreCase);
    private string _currentTarget = "";

    // Controls
    private readonly TableLayoutPanel _mainLayout = new() { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly Panel _inputPanel = new() { Dock = DockStyle.Fill, Height = 36 };
    private readonly TextBox _inputBox = new() { Dock = DockStyle.Fill, Font = new Font("Consolas", 10) };
    private readonly Button _sendBtn = new() { Text = "Send", Width = 70, Dock = DockStyle.Right };
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _statusLabel = new() { Text = "Disconnected" };

    // Server panel controls
    private readonly Panel _connectPanel = new() { Dock = DockStyle.Top, Height = 80, Padding = new Padding(8) };
    private readonly TextBox _serverBox = new() { Text = "irc.libera.chat", Width = 200 };
    private readonly TextBox _portBox = new() { Text = "6667", Width = 60 };
    private readonly TextBox _nickBox = new() { Text = "IRCUser" + new Random().Next(100, 999), Width = 110 };
    private readonly TextBox _passBox = new() { PlaceholderText = "Password (optional)", Width = 150, PasswordChar = '*' };
    private readonly Button _connectBtn = new() { Text = "Connect", Width = 80 };
    private readonly Button _disconnectBtn = new() { Text = "Disconnect", Width = 90, Enabled = false };

    public MainForm()
    {
        Text = "IRC Client (RFC 1459 / RFC 2812)";
        Size = new Size(900, 650);
        Font = new Font("Segoe UI", 9);
        MinimumSize = new Size(600, 400);

        BuildConnectPanel();

        _mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        _mainLayout.Controls.Add(_tabs, 0, 0);
        _mainLayout.Controls.Add(_inputPanel, 0, 1);

        _inputPanel.Controls.Add(_inputBox);
        _inputPanel.Controls.Add(_sendBtn);

        _status.Items.Add(_statusLabel);

        Controls.Add(_mainLayout);
        Controls.Add(_connectPanel);
        Controls.Add(_status);

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
                _currentTarget = _tabs.SelectedTab.Text;
        };
    }

    private void BuildConnectPanel()
    {
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };

        void AddLabeled(string label, Control ctrl)
        {
            layout.Controls.Add(new Label { Text = label, TextAlign = ContentAlignment.MiddleLeft, AutoSize = true, Padding = new Padding(4, 10, 0, 0) });
            ctrl.Margin = new Padding(2, 8, 4, 0);
            layout.Controls.Add(ctrl);
        }

        AddLabeled("Server:", _serverBox);
        AddLabeled("Port:", _portBox);
        AddLabeled("Nick:", _nickBox);
        AddLabeled("Pass:", _passBox);

        _connectBtn.Margin = new Padding(4, 8, 2, 0);
        _disconnectBtn.Margin = new Padding(2, 8, 2, 0);
        layout.Controls.Add(_connectBtn);
        layout.Controls.Add(_disconnectBtn);

        _connectPanel.Controls.Add(layout);

        _connectBtn.Click += OnConnect;
        _disconnectBtn.Click += OnDisconnect;
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
    }

    private async void OnConnect(object? s, EventArgs e)
    {
        if (!int.TryParse(_portBox.Text, out int port)) port = 6667;

        _irc?.Dispose();
        _irc = new IrcConnection();
        _irc.MessageReceived += OnMessage;
        _irc.Disconnected += () =>
        {
            _statusLabel.Text = "Disconnected";
            _connectBtn.Enabled = true;
            _disconnectBtn.Enabled = false;
            AppendLine("(server)", "*** Disconnected", Color.Orange);
        };

        try
        {
            AppendLine("(server)", $"*** Connecting to {_serverBox.Text}:{port}...", Color.Cyan);
            await _irc.ConnectAsync(_serverBox.Text, port, _nickBox.Text,
                string.IsNullOrWhiteSpace(_passBox.Text) ? null : _passBox.Text);
            _statusLabel.Text = $"Connected to {_serverBox.Text} as {_nickBox.Text}";
            _connectBtn.Enabled = false;
            _disconnectBtn.Enabled = true;
        }
        catch (Exception ex)
        {
            AppendLine("(server)", $"*** Error: {ex.Message}", Color.Red);
        }
    }

    private async void OnDisconnect(object? s, EventArgs e)
    {
        if (_irc != null) await _irc.QuitAsync("Leaving");
        _irc?.Dispose();
        _irc = null;
        _connectBtn.Enabled = true;
        _disconnectBtn.Enabled = false;
    }

    private void OnMessage(IrcMessage msg)
    {
        switch (msg.Command)
        {
            case "001": // RPL_WELCOME
                AppendLine("(server)", $"*** {msg.Params.LastOrDefault()}", Color.LightGreen);
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
                AppendLine(channel, $"*** {nick} joined {channel}", Color.LightBlue);
                _tabs.SelectedTab = _channels[channel].tab;
                _currentTarget = channel;
                break;
            }

            case "PART":
            {
                var channel = msg.Params[0];
                var nick = msg.PrefixNick ?? "";
                var reason = msg.Params.Length > 1 ? msg.Params[1] : "";
                AppendLine(channel, $"*** {nick} left {channel} ({reason})", Color.LightSalmon);
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

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _irc?.Dispose();
        base.OnFormClosed(e);
    }
}
