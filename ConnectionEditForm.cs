using System.Drawing;
using System.Windows.Forms;

namespace IRCClient;

// Small modal dialog for creating/editing a saved connection entry
public class ConnectionEditForm : Form
{
    private readonly TextBox _nameBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _serverBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _portBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _nickBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _passBox = new() { Dock = DockStyle.Fill, PasswordChar = '*', PlaceholderText = "optional" };
    private readonly TextBox _channelsBox = new() { Dock = DockStyle.Fill };

    public SavedConnection Result { get; private set; } = new();

    public ConnectionEditForm(SavedConnection? existing)
    {
        // See MainForm's constructor for why manual LogicalToDeviceUnits
        // conversion is used instead of the legacy AutoScale system.
        AutoScaleMode = AutoScaleMode.None;
        Text = existing == null ? "New Connection" : "Edit Connection";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9);
        ClientSize = LogicalToDeviceUnits(new Size(320, 275));
        Icon = AppIcon.Get();

        if (existing != null)
        {
            _nameBox.Text = existing.Name;
            _serverBox.Text = existing.Server;
            _portBox.Text = existing.Port.ToString();
            _nickBox.Text = existing.Nick;
            _passBox.Text = existing.Password;
            _channelsBox.Text = existing.Channels;
        }
        else
        {
            _serverBox.Text = "irc.rizon.net";
            _portBox.Text = "6667";
            _nickBox.Text = "IRCUser" + new Random().Next(100, 999);
        }

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 6,
            Height = LogicalToDeviceUnits(210),
            Padding = new Padding(LogicalToDeviceUnits(10))
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LogicalToDeviceUnits(80)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 6; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, LogicalToDeviceUnits(32)));

        void AddRow(int row, string label, Control ctrl)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            ctrl.Margin = new Padding(LogicalToDeviceUnits(2), LogicalToDeviceUnits(5), LogicalToDeviceUnits(2), LogicalToDeviceUnits(2));
            layout.Controls.Add(ctrl, 1, row);
        }

        AddRow(0, "Name:", _nameBox);
        AddRow(1, "Server:", _serverBox);
        AddRow(2, "Port:", _portBox);
        AddRow(3, "Nick:", _nickBox);
        AddRow(4, "Password:", _passBox);
        AddRow(5, "Channels:", _channelsBox);

        // Height must be set explicitly too, not just Width: with AutoScaleMode.None,
        // a Button that never gets an explicit Size falls back to WinForms' hardcoded
        // DefaultSize (75x23 logical px) unscaled, leaving it small at high DPI even
        // though everything sized via LogicalToDeviceUnits around it scales correctly.
        var okBtn = new Button { Text = "OK", DialogResult = DialogResult.OK, Size = LogicalToDeviceUnits(new Size(80, 28)) };
        var cancelBtn = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = LogicalToDeviceUnits(new Size(80, 28)) };
        okBtn.Click += (s, e) =>
        {
            Result = new SavedConnection
            {
                Name = string.IsNullOrWhiteSpace(_nameBox.Text) ? _serverBox.Text.Trim() : _nameBox.Text.Trim(),
                Server = _serverBox.Text.Trim(),
                Port = int.TryParse(_portBox.Text, out var p) ? p : 6667,
                Nick = _nickBox.Text.Trim(),
                Password = _passBox.Text,
                Channels = _channelsBox.Text.Trim()
            };
        };

        var btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = LogicalToDeviceUnits(40),
            Padding = new Padding(LogicalToDeviceUnits(10))
        };
        // Identical margins on both so they line up: the default Button margin
        // (3,3,3,3) on one and a custom margin on the other left them vertically
        // offset. Only the gap between them (left margin on cancel) differs.
        okBtn.Margin = new Padding(0);
        cancelBtn.Margin = new Padding(LogicalToDeviceUnits(6), 0, 0, 0);
        btnPanel.Controls.Add(cancelBtn);
        btnPanel.Controls.Add(okBtn);

        Controls.Add(layout);
        Controls.Add(btnPanel);

        AcceptButton = okBtn;
        CancelButton = cancelBtn;
    }
}
