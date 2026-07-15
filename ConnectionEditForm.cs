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
    private readonly TextBox _passBox = new() { Dock = DockStyle.Fill, PasswordChar = '*' };
    private readonly TextBox _channelsBox = new() { Dock = DockStyle.Fill };

    public SavedConnection Result { get; private set; } = new();

    public ConnectionEditForm(SavedConnection? existing)
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = existing == null ? "New Connection" : "Edit Connection";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(320, 275);
        Font = new Font("Segoe UI", 9);

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
            _portBox.Text = "6667";
            _nickBox.Text = "IRCUser" + new Random().Next(100, 999);
        }

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 6,
            Height = 210,
            Padding = new Padding(10)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 6; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

        void AddRow(int row, string label, Control ctrl)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            ctrl.Margin = new Padding(2, 5, 2, 2);
            layout.Controls.Add(ctrl, 1, row);
        }

        AddRow(0, "Name:", _nameBox);
        AddRow(1, "Server:", _serverBox);
        AddRow(2, "Port:", _portBox);
        AddRow(3, "Nick:", _nickBox);
        AddRow(4, "Password:", _passBox);
        AddRow(5, "Channels:", _channelsBox);

        var okBtn = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 80 };
        var cancelBtn = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 80 };
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
            Height = 40,
            Padding = new Padding(10)
        };
        cancelBtn.Margin = new Padding(4, 0, 0, 0);
        btnPanel.Controls.Add(cancelBtn);
        btnPanel.Controls.Add(okBtn);

        Controls.Add(layout);
        Controls.Add(btnPanel);

        AcceptButton = okBtn;
        CancelButton = cancelBtn;
    }
}
