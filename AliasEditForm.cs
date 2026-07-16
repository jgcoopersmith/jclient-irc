using System.Drawing;
using System.Windows.Forms;

namespace IRCClient;

// Editor for mIRC-style aliases (Tools > Alias). One alias per line:
//   /name commands
// where commands may use $1 $2 $N- $N-M $$N parameters, $+ concatenation,
// $? prompts, and | to separate multiple commands.
public class AliasEditForm : Form
{
    private readonly TextBox _text = new()
    {
        Multiline = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        AcceptsTab = true,
        Font = new Font("Consolas", 10),
        Dock = DockStyle.Fill
    };

    public string Aliases => _text.Text;

    public AliasEditForm(string aliases)
    {
        AutoScaleMode = AutoScaleMode.None;
        Text = "Aliases";
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9);
        Icon = AppIcon.Get();
        ClientSize = LogicalToDeviceUnits(new Size(560, 420));
        MinimumSize = LogicalToDeviceUnits(new Size(360, 240));

        int L(int v) => LogicalToDeviceUnits(v);

        var help = new Label
        {
            Dock = DockStyle.Top,
            Height = L(56),
            Padding = new Padding(L(8), L(6), L(8), L(2)),
            Text = "One alias per line:  /name commands\n" +
                   "Params: $1 $2  $2- (rest)  $2-4 (range)  $$1 (required)  $+ (join)  $? (prompt)\n" +
                   "Separate multiple commands with |    e.g.  /gb /join #gb    /slap /me slaps $1 with $2-"
        };

        var btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = L(44),
            Padding = new Padding(L(8))
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Size = LogicalToDeviceUnits(new Size(90, 28)) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = LogicalToDeviceUnits(new Size(90, 28)), Margin = new Padding(L(6), 0, 0, 0) };
        btnPanel.Controls.Add(cancel);
        btnPanel.Controls.Add(ok);

        _text.Text = aliases;

        Controls.Add(_text);
        Controls.Add(btnPanel);
        Controls.Add(help);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
