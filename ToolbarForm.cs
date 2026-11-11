using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Tabbar;

internal sealed class ToolbarForm : Form
{
    private readonly SettingsStore store;
    private readonly Settings settings;
    private readonly bool reserveDesktop;
    private readonly ScriptExecution execution;
    private readonly System.Windows.Forms.Timer runTimer = new() { Interval = 1000 };
    private readonly FlowLayoutPanel list = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(8, 10, 8, 8) };
    private readonly ToolTip tips = new() { AutoPopDelay = 15000 };
    private readonly NotifyIcon tray;
    private readonly GaugeSlideshow gauges;
    private readonly TopMemoryProcesses topMemoryProcesses = new();
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 62, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.FromArgb(150, 162, 180), Text = "Dra filer eller mapper hit\neller trykk + Legg til" };
    private bool registered, positioning, queuedPosition, resizing;
    private int resizeRight;
    private Rectangle? reservation;
    private readonly uint callback = Native.RegisterWindowMessage("Tabbar.AppBar.Callback");
    private readonly uint taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
    internal ToolbarForm(SettingsStore store, Settings settings, bool reserveDesktop = true)
    {
        this.store = store;
        this.settings = settings;
        this.reserveDesktop = reserveDesktop;
        gauges = new(settings.Gauges);
        execution = new ScriptExecution(Path.Combine(store.DirectoryPath, "logs"));
        Text = "Tabbar";
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(20, 26, 36);
        ForeColor = Color.FromArgb(233, 239, 247);
        Font = new Font("Segoe UI", 9);
        Icon = SystemIcons.Application;
        Width = settings.Width;
        var header = new Panel { Dock = DockStyle.Top, Height = 92, Padding = new Padding(10, 8, 8, 8) };
        var title = new Label { Text = "TABBAR", Dock = DockStyle.Top, Height = 30, Font = new Font("Segoe UI", 11, FontStyle.Bold) };
        var menuButton = new Button { Text = "•••", Dock = DockStyle.Right, Width = 32, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };
        menuButton.FlatAppearance.BorderSize = 0;
        menuButton.Click += (_, _) => ShowMenu(menuButton, new Point(0, menuButton.Height));
        var add = new Button { Text = "+ Legg til", Dock = DockStyle.Bottom, Height = 30, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(39, 61, 83), Cursor = Cursors.Hand };
        add.FlatAppearance.BorderSize = 0;
        add.Click += (_, _) => ShowAddMenu(add);
        title.Controls.Add(menuButton);
        header.Controls.Add(title);
        header.Controls.Add(add);
        var grip = new Panel { Dock = DockStyle.Left, Width = 5, BackColor = Color.FromArgb(67, 145, 164), Cursor = Cursors.SizeWE };
        tips.SetToolTip(grip, "Dra for å endre bredde (100–600 piksler)");
        grip.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { resizing = true; resizeRight = Right; grip.Capture = true; } };
        grip.MouseMove += (_, _) => { if (resizing) { settings.Width = Math.Clamp(resizeRight - Cursor.Position.X, 100, MaximumWidth()); PositionBar(); } };
        grip.MouseUp += (_, _) => { if (resizing) { resizing = false; grip.Capture = false; Save(); } };
        grip.MouseCaptureChanged += (_, _) => { if (resizing && !grip.Capture) { resizing = false; Save(); } };
        Controls.Add(list);
        Controls.Add(status);
        Controls.Add(gauges);
        Controls.Add(topMemoryProcesses);
        Controls.Add(header);
        Controls.Add(grip);
        EnableDrop(this);
        EnableDrop(list);
        EnableDrop(status);
        EnableDrop(header);
        EnableDrop(gauges);
        EnableDrop(gauges.Memory);
        EnableDrop(gauges.Codex);
        EnableDrop(gauges.Copilot);
        EnableDrop(topMemoryProcesses);
        list.Resize += (_, _) => ResizeCards();
        tray = new NotifyIcon { Icon = Icon, Text = "Tabbar – snarveier på skrivebordet", Visible = true };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Right) ShowMenu(this, PointToClient(Cursor.Position)); else { Show(); Activate(); } };
        RefreshScripts();
        runTimer.Tick += (_, _) =>
        {
            if (SettingsStore.RefreshResults(settings)) Save();
            foreach (Button card in list.Controls.OfType<Button>())
                if (card.Tag is ScriptEntry entry) UpdateCardState(card, entry);
        };
        runTimer.Start();
    }

    private Screen SelectedScreen => Screen.AllScreens.FirstOrDefault(s => s.DeviceName == settings.Monitor) ?? Screen.PrimaryScreen!;
    private int MaximumWidth() => Math.Max(100, Math.Min(600, SelectedScreen.Bounds.Width / 2));
    protected override void OnShown(EventArgs e) { base.OnShown(e); if (reserveDesktop) RegisterBar(); }
    private Native.AppBarData Data() => new() { Size = (uint)Marshal.SizeOf<Native.AppBarData>(), Window = Handle, Callback = callback, Edge = 2 };
    private void RegisterBar()
    {
        reservation = null;
        var data = Data();
        registered = Native.SHAppBarMessage(0, ref data) != UIntPtr.Zero;
        if (!registered) throw new InvalidOperationException("Windows kunne ikke reservere plass til Tabbar. Start appen fra en vanlig Windows-skrivebordsøkt.\nShell-vindu: " + Native.GetShellWindow());
        PositionBar();
    }
    internal void PositionBar()
    {
        if (!registered || positioning) return;
        positioning = true;
        try
        {
            var bounds = SelectedScreen.Bounds;
            settings.Width = Math.Clamp(settings.Width, 100, MaximumWidth());
            var data = Data();
            data.Rect = new() { Left = bounds.Right - settings.Width, Top = bounds.Top, Right = bounds.Right, Bottom = bounds.Bottom };
            Native.SHAppBarMessage(2, ref data);
            data.Rect.Left = data.Rect.Right - settings.Width;
            var proposed = Rectangle.FromLTRB(data.Rect.Left, data.Rect.Top, data.Rect.Right, data.Rect.Bottom);
            if (reservation == proposed && Bounds == proposed) return;
            Native.SHAppBarMessage(3, ref data);
            reservation = Rectangle.FromLTRB(data.Rect.Left, data.Rect.Top, data.Rect.Right, data.Rect.Bottom);
            SetBounds(data.Rect.Left, data.Rect.Top, data.Rect.Right - data.Rect.Left, data.Rect.Bottom - data.Rect.Top);
        }
        finally { positioning = false; }
    }
    private void QueuePosition()
    {
        if (queuedPosition || IsDisposed || !IsHandleCreated) return;
        queuedPosition = true;
        BeginInvoke(() => { queuedPosition = false; if (!IsDisposed) PositionBar(); });
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == taskbarCreated && IsHandleCreated) { registered = false; BeginInvoke(RegisterBar); }
        if (m.Msg == callback)
        {
            switch (m.WParam.ToInt32())
            {
                case 1: if (!positioning) QueuePosition(); break;
                case 2:
                    // Exclusive full-screen apps follow Windows' appbar convention.
                    TopMost = m.LParam == IntPtr.Zero;
                    if (!TopMost) Native.SetWindowPos(Handle, new IntPtr(1), 0, 0, 0, 0, 0x13);
                    break;
                case 3: if (m.LParam != IntPtr.Zero) Hide(); else Show(); break;
            }
        }
        if (m.Msg == 0x007e || m.Msg == 0x02e0) QueuePosition();
        base.WndProc(ref m);
        if (registered && (m.Msg == 0x0006 || m.Msg == 0x0047))
        {
            var data = Data();
            Native.SHAppBarMessage(m.Msg == 0x0006 ? 6u : 9u, ref data);
        }
    }
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (registered) { registered = false; var data = Data(); Native.SHAppBarMessage(1, ref data); }
        tray.Visible = false;
        tray.Dispose();
        tips.Dispose();
        runTimer.Dispose();
        base.OnFormClosed(e);
    }
    private void EnableDrop(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += (_, e) => e.Effect = e.Data?.GetData(DataFormats.FileDrop) is string[] paths && paths.Any(p => ShortcutTargets.Create(p) is not null) ? DragDropEffects.Copy : DragDropEffects.None;
        control.DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) AddFiles(paths); };
    }
    internal void AddFiles(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var path in paths)
        {
            var entry = ShortcutTargets.Create(path);
            if (entry is null || settings.Scripts.Any(s => ShortcutTargets.SameTarget(s, entry))) continue;
            settings.Scripts.Add(entry);
            added++;
        }
        if (added > 0) { Save(); RefreshScripts(); }
    }
    private void PickFiles()
    {
        using var dialog = new OpenFileDialog { Filter = "Programmer, script og snarveier|*.exe;*.bat;*.cmd;*.ps1;*.lnk|Programmer (*.exe)|*.exe|Script (*.bat;*.cmd;*.ps1)|*.bat;*.cmd;*.ps1|Windows-snarveier (*.lnk)|*.lnk", DereferenceLinks = false, Multiselect = true, Title = "Legg til snarveier" };
        if (dialog.ShowDialog(this) == DialogResult.OK) AddFiles(dialog.FileNames);
    }
    private void PickFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Velg mappe", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) AddFiles([dialog.SelectedPath]);
    }
    private void PickWebsite()
    {
        using var dialog = new WebsiteDialog();
        if (dialog.ShowDialog(this) == DialogResult.OK) AddFiles([dialog.Address]);
    }
    private void AddMenuItems(ToolStripItemCollection items)
    {
        items.Add("Legg til program eller script …", null, (_, _) => PickFiles());
        items.Add("Legg til mappe …", null, (_, _) => PickFolder());
        items.Add("Legg til nettside …", null, (_, _) => PickWebsite());
    }
    private void ShowAddMenu(Control source)
    {
        var menu = new ContextMenuStrip();
        AddMenuItems(menu.Items);
        menu.Closed += (_, _) => { if (!IsDisposed && IsHandleCreated) BeginInvoke(menu.Dispose); else menu.Dispose(); };
        menu.Show(source, new Point(0, source.Height));
    }
    private void ResizeCards()
    {
        foreach (Button card in list.Controls.OfType<Button>())
        {
            card.Width = Math.Max(50, list.ClientSize.Width - list.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 2);
            var size = Math.Min(Math.Clamp(settings.IconHeight, 16, 128), Math.Max(16, card.Width - card.Padding.Horizontal - 6));
            if (card.Tag is ScriptEntry entry && card.Image?.Height != size)
            {
                var old = card.Image; card.Image = ScriptIcons.Load(entry, size); old?.Dispose();
            }
        }
    }
    private void RefreshScripts()
    {
        list.SuspendLayout();
        while (list.Controls.Count > 0) { var c = list.Controls[0]; list.Controls.RemoveAt(0); c.Dispose(); }
        foreach (var entry in settings.Scripts)
        {
            var exists = entry.Exists;
            var card = new Button
            {
                Height = Math.Clamp(settings.IconHeight, 16, 128) + 76, Margin = new Padding(0, 0, 0, 8), FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(32, 42, 56), ForeColor = exists ? ForeColor : Color.Salmon,
                Text = entry.Name + (exists ? "" : "\nMangler fil"), TextAlign = ContentAlignment.BottomCenter,
                ImageAlign = ContentAlignment.TopCenter, Padding = new Padding(4, 8, 4, 6), Cursor = Cursors.Hand,
                AccessibleName = (entry.IsScript ? "Kjør " : "Åpne ") + entry.Name, UseMnemonic = false, AutoEllipsis = true, Tag = entry
            };
            card.FlatAppearance.BorderColor = Color.FromArgb(48, 64, 83);
            card.FlatAppearance.MouseOverBackColor = Color.FromArgb(42, 62, 82);
            card.Image = ScriptIcons.Load(entry, Math.Clamp(settings.IconHeight, 16, 128));
            card.Disposed += (_, _) => card.Image?.Dispose();
            card.Click += async (_, _) => await RunScriptAsync(entry);
            var menu = new ContextMenuStrip();
            menu.Items.Add("Egenskaper (navn og ikon) …", null, (_, _) =>
            {
                using var dialog = new ScriptPropertiesDialog(entry, Path.Combine(store.DirectoryPath, "icons"));
                if (dialog.ShowDialog(this) == DialogResult.OK) { Save(); RefreshScripts(); }
            });
            var logItem = menu.Items.Add("Vis siste logg …", null, (_, _) =>
            {
                if (entry.LastLogPath is not null) new LogViewer(entry.Name, entry.LastLogPath).Show();
            });
            var admin = new ToolStripMenuItem("Kjør som administrator") { Checked = entry.RunAsAdministrator };
            admin.Click += (_, _) => { entry.RunAsAdministrator = !entry.RunAsAdministrator; Save(); RefreshScripts(); };
            if (entry.CanElevate) menu.Items.Add(admin); else admin.Dispose();
            menu.Items.Add(new ToolStripSeparator());
            if (entry.TargetKind != ShortcutKind.Website)
                menu.Items.Add("Vis i Filutforsker", null, (_, _) => { try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + entry.Path + "\"") { UseShellExecute = true }); } catch (Exception e) { MessageBox.Show(e.Message); } });
            var folder = new ToolStripMenuItem("Bruk filens mappe som arbeidsmappe") { Checked = entry.UseScriptFolder, CheckOnClick = true };
            folder.CheckedChanged += (_, _) => { entry.UseScriptFolder = folder.Checked; Save(); };
            if (entry.CanElevate) menu.Items.Add(folder); else folder.Dispose();
            menu.Items.Add("Flytt opp", null, (_, _) => MoveEntry(entry, -1));
            menu.Items.Add("Flytt ned", null, (_, _) => MoveEntry(entry, 1));
            menu.Items.Add(new ToolStripSeparator());
            var removeItem = menu.Items.Add("Fjern fra toolbar", null, (_, _) => { settings.Scripts.Remove(entry); Save(); RefreshScripts(); });
            logItem.Visible = entry.IsScript;
            menu.Opening += (_, _) => { logItem.Enabled = entry.LastLogPath is not null && File.Exists(entry.LastLogPath); removeItem.Enabled = !entry.IsScript || !execution.IsRunning(entry.Path); };
            card.ContextMenuStrip = menu;
            card.Disposed += (_, _) => menu.Dispose();
            EnableDrop(card);
            UpdateCardState(card, entry);
            list.Controls.Add(card);
        }
        status.Text = settings.Scripts.Count == 0 ? "Dra filer eller mapper hit\neller trykk + Legg til" : $"{settings.Scripts.Count} snarveier\nDra inn flere her";
        list.ResumeLayout();
        ResizeCards();
    }

    private void UpdateCardState(Button card, ScriptEntry entry)
    {
        var running = entry.IsScript && execution.IsRunning(entry.Path);
        var exists = entry.Exists;
        var state = running ? "Kjører …" : !exists ? "Mangler " + (entry.TargetKind == ShortcutKind.Folder ? "mappe" : "fil") : entry.IsScript ? "Klar" : ShortcutTargets.Label(entry);
        var color = running ? Color.FromArgb(238, 188, 89) : !exists ? Color.Salmon : ForeColor;
        var label = entry.Name + "\n" + state;
        if (card.Text != label) card.Text = label;
        card.ForeColor = color;
        card.FlatAppearance.BorderColor = running ? color : Color.FromArgb(48, 64, 83);
        card.AccessibleDescription = state + (entry.CanElevate && entry.RunAsAdministrator ? ", administrator" : "");
        var tooltip = entry.Path + "\n" + state + (entry.LastRunAt is { } date ? $"\nSist startet: {date.LocalDateTime:g}" : "")
            + (entry.CanElevate ? (entry.RunAsAdministrator ? "\nKjøres som administrator (UAC)" : "\nKjøres med vanlige brukerrettigheter")
                + "\nArbeidsmappe: " + (entry.UseScriptFolder ? Path.GetDirectoryName(entry.Path) : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)) : "\nÅpnes gjennom Windows")
            + "\nHøyreklikk for egenskaper" + (entry.IsScript ? " og logg" : "");
        if (tips.GetToolTip(card) != tooltip) tips.SetToolTip(card, tooltip);
    }

    internal async Task RunScriptAsync(ScriptEntry entry)
    {
        if (entry.IsScript && execution.IsRunning(entry.Path)) return;
        try
        {
            if (!entry.IsScript)
            {
                using var opened = Process.Start(ShortcutTargets.CreateOpenStartInfo(entry));
                entry.LastRunAt = DateTimeOffset.Now;
                entry.LastRunError = null;
                entry.LastExitCode = null;
                return;
            }
            var outcome = await execution.RunAsync(entry, settings.KeepConsole, log =>
            {
                entry.LastLogPath = log;
                entry.LastRunAt = DateTimeOffset.Now;
                entry.LastExitCode = null;
                entry.LastRunError = null;
                Save();
                RefreshScripts();
            });
            entry.LastExitCode = outcome.Result.ExitCode;
            entry.LastRunError = outcome.Result.Error;
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { /* UAC was cancelled; preserve previous result. */ }
        catch (Exception e)
        {
            entry.LastRunError = e.Message;
            entry.LastExitCode = -1;
            if (!IsDisposed) MessageBox.Show(this, e.Message + "\n\n" + entry.Path, "Kunne ikke åpne snarvei", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (!IsDisposed) { Save(); RefreshScripts(); }
        }
    }
    private void MoveEntry(ScriptEntry entry, int delta)
    {
        var from = settings.Scripts.IndexOf(entry);
        var to = from + delta;
        if (to < 0 || to >= settings.Scripts.Count) return;
        settings.Scripts.RemoveAt(from);
        settings.Scripts.Insert(to, entry);
        Save(); RefreshScripts();
    }
    private void ShowMenu(Control source, Point point)
    {
        var menu = new ContextMenuStrip();
        AddMenuItems(menu.Items);
        var startup = new ToolStripMenuItem("Start med Windows") { Checked = Startup.Enabled };
        startup.Click += (_, _) => { try { Startup.Set(!Startup.Enabled); } catch (Exception e) { MessageBox.Show(this, e.Message, "Kunne ikke endre oppstart"); } };
        menu.Items.Add(startup);
        menu.Items.Add("Innstillinger …", null, async (_, _) =>
        {
            using var dialog = new GaugeSettingsDialog(settings.Gauges, settings.IconHeight);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            settings.Gauges = dialog.Result;
            settings.IconHeight = dialog.IconHeight;
            Save();
            RefreshScripts();
            gauges.Configure(settings.Gauges);
            await gauges.RefreshQuotasAsync();
        });
        var console = new ToolStripMenuItem("Behold scriptvindu åpent") { Checked = settings.KeepConsole, CheckOnClick = true };
        console.CheckedChanged += (_, _) => { settings.KeepConsole = console.Checked; Save(); };
        menu.Items.Add(console);
        var monitors = new ToolStripMenuItem("Skjerm");
        foreach (var screen in Screen.AllScreens)
        {
            var item = new ToolStripMenuItem(screen.DeviceName + (screen.Primary ? " (hovedskjerm)" : "")) { Checked = screen.DeviceName == SelectedScreen.DeviceName };
            item.Click += (_, _) => { settings.Monitor = screen.DeviceName; PositionBar(); Save(); };
            monitors.DropDownItems.Add(item);
        }
        menu.Items.Add(monitors);
        menu.Items.Add("Tilbakestill bredde (150 px)", null, (_, _) => { settings.Width = 150; PositionBar(); Save(); });
        menu.Items.Add("Åpne skrivebordet", null, (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", "\"" + Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) + "\"") { UseShellExecute = true }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Avslutt Tabbar", null, (_, _) => Close());
        menu.Closed += (_, _) => { if (!IsDisposed && IsHandleCreated) BeginInvoke(menu.Dispose); else menu.Dispose(); };
        menu.Show(source, point);
    }
    private void Save()
    {
        try { store.Save(settings); }
        catch (Exception e) { MessageBox.Show(this, "Kunne ikke lagre innstillinger:\n" + e.Message, "Tabbar"); }
    }
}
