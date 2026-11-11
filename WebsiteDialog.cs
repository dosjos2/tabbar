namespace Tabbar;

internal sealed class WebsiteDialog : Form
{
    private readonly TextBox address = new() { Dock = DockStyle.Fill, PlaceholderText = "https://eksempel.no" };
    internal string Address => address.Text.Trim();

    internal WebsiteDialog()
    {
        Text = "Legg til nettside";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(460, 150);
        Font = new Font("Segoe UI", 9);
        BackColor = Color.FromArgb(20, 26, 36);
        ForeColor = Color.FromArgb(233, 239, 247);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "Nettadresse (http eller https)", AutoSize = true });
        layout.Controls.Add(address);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var add = new Button { Text = "Legg til", AutoSize = true };
        add.Click += (_, _) =>
        {
            if (!ShortcutTargets.IsWebsite(Address))
            {
                MessageBox.Show(this, "Skriv inn en gyldig nettadresse som begynner med http:// eller https://.", "Ugyldig nettadresse");
                address.Focus();
                return;
            }
            DialogResult = DialogResult.OK;
        };
        var cancel = new Button { Text = "Avbryt", AutoSize = true, DialogResult = DialogResult.Cancel };
        foreach (var button in new[] { add, cancel })
        {
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = Color.FromArgb(39, 61, 83);
            button.FlatAppearance.BorderSize = 0;
        }
        buttons.Controls.AddRange([add, cancel]);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = add;
        CancelButton = cancel;
    }
}
