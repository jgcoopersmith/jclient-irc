using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace IRCClient;

// Channel management dialog (right-click a channel tab). Reads the channel's
// current modes/topic, lets the user edit them, and emits the minimal set of
// MODE/TOPIC commands needed to apply the changes.
public class ChannelSettingsForm : Form
{
    private readonly TextBox _topicBox = new() { Dock = DockStyle.Fill };
    private readonly ListBox _banList = new() { Dock = DockStyle.Fill, IntegralHeight = false, SelectionMode = SelectionMode.MultiExtended };

    // Simple (argument-less) mode flags
    private readonly CheckBox _cbOpsTopic = new() { Text = "Ops set topic" };     // +t
    private readonly CheckBox _cbNoExternal = new() { Text = "No external msgs" }; // +n
    private readonly CheckBox _cbInviteOnly = new() { Text = "Invite only" };      // +i
    private readonly CheckBox _cbModerated = new() { Text = "Moderated" };         // +m
    private readonly CheckBox _cbPrivate = new() { Text = "Private" };             // +p
    private readonly CheckBox _cbSecret = new() { Text = "Secret" };               // +s

    // Argument-taking modes
    private readonly CheckBox _cbKey = new() { Text = "Channel key" };  // +k <key>
    private readonly TextBox _keyBox = new();
    private readonly CheckBox _cbLimit = new() { Text = "Max users" };  // +l <n>
    private readonly TextBox _limitBox = new();

    private readonly string _channel;
    private readonly string _origTopic;
    private readonly HashSet<char> _origFlags;
    private readonly string? _origKey;
    private readonly string? _origLimit;
    private readonly Action<string> _send;

    public ChannelSettingsForm(string channel, string topic, string modes, Action<string> send)
    {
        _channel = channel;
        _origTopic = topic;
        _send = send;
        (_origFlags, _origKey, _origLimit) = ParseModes(modes);

        AutoScaleMode = AutoScaleMode.None;
        Text = $"Channel Settings — {channel}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9);
        Icon = AppIcon.Get();
        ClientSize = LogicalToDeviceUnits(new Size(500, 470));

        int L(int v) => LogicalToDeviceUnits(v);

        // --- Topic ---
        Controls.Add(new Label { Text = "Topic:", Location = new Point(L(12), L(14)), Size = new Size(L(60), L(20)) });
        _topicBox.Location = new Point(L(72), L(11));
        _topicBox.Size = new Size(L(416), L(24));
        _topicBox.Text = topic;
        _topicBox.Dock = DockStyle.None;
        Controls.Add(_topicBox);

        // --- Bans ---
        Controls.Add(new Label { Text = "Bans:", Location = new Point(L(12), L(48)), Size = new Size(L(60), L(20)) });
        _banList.Dock = DockStyle.None;
        _banList.Location = new Point(L(12), L(70));
        _banList.Size = new Size(L(366), L(120));
        Controls.Add(_banList);
        var removeBanBtn = new Button { Text = "Remove", Location = new Point(L(388), L(70)), Size = new Size(L(100), L(28)) };
        removeBanBtn.Click += (s, e) =>
        {
            foreach (string mask in _banList.SelectedItems.Cast<string>().ToList())
            {
                _send($"MODE {_channel} -b {mask}");
                _banList.Items.Remove(mask);
            }
        };
        Controls.Add(removeBanBtn);

        // --- Mode flags: 6 checkboxes on the left ---
        var leftFlags = new[] { _cbOpsTopic, _cbNoExternal, _cbInviteOnly, _cbModerated, _cbPrivate, _cbSecret };
        int y = L(210);
        foreach (var cb in leftFlags)
        {
            cb.Location = new Point(L(12), y);
            cb.Size = new Size(L(200), L(24));
            Controls.Add(cb);
            y += L(30);
        }
        _cbOpsTopic.Checked = _origFlags.Contains('t');
        _cbNoExternal.Checked = _origFlags.Contains('n');
        _cbInviteOnly.Checked = _origFlags.Contains('i');
        _cbModerated.Checked = _origFlags.Contains('m');
        _cbPrivate.Checked = _origFlags.Contains('p');
        _cbSecret.Checked = _origFlags.Contains('s');

        // --- Key / limit: 2 checkboxes with entry fields on the right ---
        _cbKey.Location = new Point(L(250), L(210));
        _cbKey.Size = new Size(L(150), L(24));
        _cbKey.Checked = _origKey != null;
        Controls.Add(_cbKey);
        _keyBox.Location = new Point(L(270), L(236));
        _keyBox.Size = new Size(L(200), L(24));
        _keyBox.Text = _origKey ?? "";
        _keyBox.Enabled = _cbKey.Checked;
        _cbKey.CheckedChanged += (s, e) => _keyBox.Enabled = _cbKey.Checked;
        Controls.Add(_keyBox);

        _cbLimit.Location = new Point(L(250), L(276));
        _cbLimit.Size = new Size(L(150), L(24));
        _cbLimit.Checked = _origLimit != null;
        Controls.Add(_cbLimit);
        _limitBox.Location = new Point(L(270), L(302));
        _limitBox.Size = new Size(L(200), L(24));
        _limitBox.Text = _origLimit ?? "";
        _limitBox.Enabled = _cbLimit.Checked;
        _cbLimit.CheckedChanged += (s, e) => _limitBox.Enabled = _cbLimit.Checked;
        Controls.Add(_limitBox);

        // --- OK / Cancel ---
        var okBtn = new Button { Text = "OK", DialogResult = DialogResult.OK, Size = LogicalToDeviceUnits(new Size(90, 28)), Location = new Point(L(288), L(428)) };
        var cancelBtn = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = LogicalToDeviceUnits(new Size(90, 28)), Location = new Point(L(398), L(428)) };
        okBtn.Click += (s, e) => ApplyChanges();
        Controls.Add(okBtn);
        Controls.Add(cancelBtn);
        AcceptButton = okBtn;
        CancelButton = cancelBtn;
    }

    public void AddBan(string mask) => _banList.Items.Add(mask);

    private void ApplyChanges()
    {
        if (_topicBox.Text != _origTopic)
            _send($"TOPIC {_channel} :{_topicBox.Text}");

        var changes = new List<(char sign, char mode, string? arg)>();

        void Flag(char m, bool now)
        {
            bool was = _origFlags.Contains(m);
            if (now && !was) changes.Add(('+', m, null));
            else if (!now && was) changes.Add(('-', m, null));
        }
        Flag('t', _cbOpsTopic.Checked);
        Flag('n', _cbNoExternal.Checked);
        Flag('i', _cbInviteOnly.Checked);
        Flag('m', _cbModerated.Checked);
        Flag('p', _cbPrivate.Checked);
        Flag('s', _cbSecret.Checked);

        // key
        var newKey = _keyBox.Text.Trim();
        bool hadKey = _origKey != null;
        if (_cbKey.Checked && newKey.Length > 0)
        {
            if (!hadKey || newKey != _origKey)
            {
                if (hadKey) changes.Add(('-', 'k', _origKey)); // replace: drop old first
                changes.Add(('+', 'k', newKey));
            }
        }
        else if (!_cbKey.Checked && hadKey)
        {
            changes.Add(('-', 'k', _origKey));
        }

        // limit
        var newLimit = _limitBox.Text.Trim();
        bool hadLimit = _origLimit != null;
        if (_cbLimit.Checked && int.TryParse(newLimit, out _))
        {
            if (!hadLimit || newLimit != _origLimit)
                changes.Add(('+', 'l', newLimit));
        }
        else if (!_cbLimit.Checked && hadLimit)
        {
            changes.Add(('-', 'l', null)); // -l takes no argument
        }

        if (changes.Count == 0) return;

        var sb = new StringBuilder($"MODE {_channel} ");
        char lastSign = '\0';
        var args = new List<string>();
        foreach (var (sign, mode, arg) in changes)
        {
            if (sign != lastSign) { sb.Append(sign); lastSign = sign; }
            sb.Append(mode);
            if (arg != null) args.Add(arg);
        }
        if (args.Count > 0) { sb.Append(' '); sb.Append(string.Join(" ", args)); }
        _send(sb.ToString());
    }

    // Parse a channel mode string like "+tnk thekey" / "+tnl 50" / "+tnkl key 50"
    // into (flag set, key, limit).
    private static (HashSet<char> flags, string? key, string? limit) ParseModes(string modes)
    {
        var flags = new HashSet<char>();
        string? key = null, limit = null;
        if (string.IsNullOrWhiteSpace(modes)) return (flags, key, limit);

        var parts = modes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var letters = parts[0].TrimStart('+');
        int argi = 1;
        foreach (var ch in letters)
        {
            if (ch == 'k') { if (argi < parts.Length) key = parts[argi++]; flags.Add('k'); }
            else if (ch == 'l') { if (argi < parts.Length) limit = parts[argi++]; flags.Add('l'); }
            else flags.Add(ch);
        }
        return (flags, key, limit);
    }
}
