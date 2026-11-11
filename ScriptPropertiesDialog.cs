using System.Drawing.Drawing2D;

namespace Tabbar;

internal static class ScriptIcons
{
    internal static Image Load(ScriptEntry entry, int size = 32)
    {
        using var source = LoadOriginal(entry, size);
        return Resize(source, size);
    }

    private static Image LoadOriginal(ScriptEntry entry, int size)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(entry.IconPath) && File.Exists(entry.IconPath)) return LoadFile(entry.IconPath, size);
        }
        catch (Exception e) when (e is IOException or ArgumentException or OutOfMemoryException) { }
        if (entry.TargetKind == ShortcutKind.Website) return SystemIcons.Information.ToBitmap();
        if (entry.TargetKind is ShortcutKind.Folder or ShortcutKind.WindowsShortcut)
        {
            // Ask the shell for the folder or shortcut's actual icon. The HICON is caller-owned.
            Native.SHGetFileInfo(entry.Path, 0, out var info, (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.ShellFileInfo>(), 0x100);
            if (info.Icon != IntPtr.Zero)
            {
                try { using var icon = Icon.FromHandle(info.Icon); return icon.ToBitmap(); }
                finally { Native.DestroyIcon(info.Icon); }
            }
        }
        try { using var icon = Icon.ExtractAssociatedIcon(entry.Path); return (icon ?? SystemIcons.Application).ToBitmap(); }
        catch { return SystemIcons.Application.ToBitmap(); }
    }

    internal static Image LoadFile(string path, int size = 32)
    {
        using var source = ReadImage(path, size);
        return Resize(source, size);
    }
    private static Image Resize(Image source, int size)
    {
        var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        var scale = Math.Min((float)size / source.Width, (float)size / source.Height);
        var width = source.Width * scale;
        var height = source.Height * scale;
        graphics.DrawImage(source, (size - width) / 2, (size - height) / 2, width, height);
        return bitmap;
    }
    private static Image ReadImage(string path, int size)
    {
        if (!Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase)) return Image.FromFile(path);
        using var icon = new Icon(path, size, size);
        return icon.ToBitmap();
    }
}

internal sealed class ScriptPropertiesDialog : Form
{
    private readonly TextBox name = new() { Dock = DockStyle.Fill, MaxLength = 80 };
    private readonly CheckBox admin = new() { Text = "Kjør som administrator (UAC)", AutoSize = true };
    private readonly CheckBox scriptFolder = new() { Text = "Bruk filens mappe som arbeidsmappe", AutoSize = true };
    private readonly PictureBox preview = new() { Width = 42, Height = 42, SizeMode = PictureBoxSizeMode.CenterImage };
    private readonly ScriptEntry entry;
    private readonly string iconDirectory;
    private string? selectedIcon;
    internal ScriptPropertiesDialog(ScriptEntry entry, string iconDirectory)
    {
        this.entry = entry;
        this.iconDirectory = iconDirectory;
        selectedIcon = entry.IconPath;
        Text = "Egenskaper – " + entry.Name;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Segoe UI", 9);
        BackColor = Color.FromArgb(20, 26, 36);
        ForeColor = Color.FromArgb(233, 239, 247);
        ClientSize = new Size(440, 330);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 8 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        if (!entry.CanElevate)
        {
            layout.RowStyles[3].Height = 0;
            layout.RowStyles[4].Height = 0;
            ClientSize = new Size(440, 266);
        }
        layout.Controls.Add(new Label { Text = "Navn i toolbaren", AutoSize = true }, 0, 0);
        name.Text = entry.Name;
        layout.Controls.Add(name, 0, 1);
        var iconRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
        preview.Image = ScriptIcons.Load(entry);
        var choose = new Button { Text = "Velg ikon …", Width = 110, Height = 30, Margin = new Padding(8, 6, 3, 0) };
        choose.Click += (_, _) => ChooseIcon();
        var reset = new Button { Text = "Standardikon", Width = 110, Height = 30, Margin = new Padding(3, 6, 3, 0) };
        reset.Click += (_, _) => { selectedIcon = null; ReplacePreview(ScriptIcons.Load(new ScriptEntry { Path = entry.Path, Kind = entry.Kind })); };
        iconRow.Controls.AddRange([preview, choose, reset]);
        layout.Controls.Add(iconRow, 0, 2);
        admin.Checked = entry.RunAsAdministrator;
        admin.Enabled = entry.CanElevate;
        admin.Visible = entry.CanElevate;
        layout.Controls.Add(admin, 0, 3);
        scriptFolder.Checked = entry.UseScriptFolder;
        scriptFolder.Enabled = entry.CanElevate;
        scriptFolder.Visible = entry.CanElevate;
        layout.Controls.Add(scriptFolder, 0, 4);
        layout.Controls.Add(new Label { Text = entry.Path, Dock = DockStyle.Fill, AutoEllipsis = true, UseMnemonic = false, ForeColor = Color.FromArgb(150, 162, 180) }, 0, 5);
        layout.Controls.Add(new Label(), 0, 6);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var save = new Button { Text = "Lagre", Width = 90, Height = 28 };
        save.Click += (_, _) => SaveEntry();
        var cancel = new Button { Text = "Avbryt", Width = 90, Height = 28, DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange([save, cancel]);
        layout.Controls.Add(buttons, 0, 7);
        Controls.Add(layout);
        foreach (var button in new[] { choose, reset, save, cancel })
        {
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = Color.FromArgb(39, 61, 83);
            button.FlatAppearance.BorderSize = 0;
        }
        AcceptButton = save;
        CancelButton = cancel;
    }

    private void ChooseIcon()
    {
        using var dialog = new OpenFileDialog { Title = "Velg eget ikon", Filter = "Ikon eller bilde|*.ico;*.png;*.jpg;*.jpeg;*.bmp" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { var image = ScriptIcons.LoadFile(dialog.FileName); ReplacePreview(image); selectedIcon = dialog.FileName; }
        catch (Exception e) { MessageBox.Show(this, "Kunne ikke lese bildet:\n" + e.Message, "Ikon"); }
    }
    private void ReplacePreview(Image image) { var old = preview.Image; preview.Image = image; old?.Dispose(); }

    private void SaveEntry()
    {
        if (string.IsNullOrWhiteSpace(name.Text)) { MessageBox.Show(this, "Skriv inn et navn.", "Navn mangler"); name.Focus(); return; }
        try
        {
            var icon = selectedIcon;
            if (icon is not null && !string.Equals(icon, entry.IconPath, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(iconDirectory);
                var destination = Path.Combine(iconDirectory, Guid.NewGuid().ToString("N") + ".png");
                using var image = ScriptIcons.LoadFile(icon, 128);
                image.Save(destination, System.Drawing.Imaging.ImageFormat.Png);
                icon = destination;
            }
            entry.Name = name.Text.Trim();
            entry.IconPath = icon;
            entry.RunAsAdministrator = admin.Checked;
            entry.UseScriptFolder = scriptFolder.Checked;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception e) { MessageBox.Show(this, "Kunne ikke lagre ikonet:\n" + e.Message, "Tabbar"); }
    }
    protected override void Dispose(bool disposing) { if (disposing) preview.Image?.Dispose(); base.Dispose(disposing); }
}
