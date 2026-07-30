using System.Drawing;
using System.Windows.Forms;

namespace IRCClient;

// Editor for the ignore list (Tools > Ignore). One mask per line:
//   nick               matched as nick!*@*
//   nick!user@host     matched in full
// with * and ? wildcards allowed anywhere. Blank lines and lines starting
// with # are ignored, so the list can be annotated.
public class IgnoreEditForm : Form
{
    private readonly TextBox _text = new()
    {
        Multiline = true,
        // Same reason as the alias editor: without this, Enter fires the
        // form's AcceptButton instead of starting a new line.
        AcceptsReturn = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Font = new Font("Consolas", 10),
        Dock = DockStyle.Fill
    };

    public string Masks => _text.Text;

    public IgnoreEditForm(string masks)
    {
        AutoScaleMode = AutoScaleMode.None;
        Text = "Ignore";
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
            Text = "One mask per line. Messages, notices and CTCPs from anyone matching are dropped.\n" +
                   "A bare nick means nick!*@*.  Wildcards: * (any run) and ? (one character).\n" +
                   "e.g.  spambot    *!*@badhost.com    troll*!*@*    # lines starting with # are notes"
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

        _text.Text = masks;

        Controls.Add(_text);
        Controls.Add(btnPanel);
        Controls.Add(help);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
