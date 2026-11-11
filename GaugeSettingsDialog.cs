using System.Diagnostics;

namespace Tabbar;

internal sealed class GaugeSettingsDialog : Form
{
    private readonly CheckBox rotate = new() { Text = "Bytt måler automatisk", AutoSize = true };
    private readonly NumericUpDown slideSeconds = new() { Minimum = 3, Maximum = 120, Width = 70 };
    private readonly NumericUpDown refreshMinutes = new() { Minimum = 1, Maximum = 60, Width = 70 };
    private readonly NumericUpDown iconHeight = new() { Minimum = 16, Maximum = 128, Width = 70 };
    private readonly CancellationTokenSource lifetime = new();
    private readonly ProviderEditor codex;
    private readonly ProviderEditor copilot;
    internal GaugeSettings Result { get; private set; }
    internal int IconHeight => (int)iconHeight.Value;

    internal GaugeSettingsDialog(GaugeSettings settings, int shortcutIconHeight = 32)
    {
        Result = settings.Copy(); Result.Normalize();
        Text = "Innstillinger · forbruk, målere og snarveier";
        ClientSize = new Size(660, 700);
        MinimumSize = new Size(600, 620);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(20, 26, 36); ForeColor = Color.FromArgb(233, 239, 247);
        Font = new Font("Segoe UI", 9);
        var content = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, Padding = new Padding(16) };
        var intro = new Label { Width = 600, Height = 76, Text = "Koble til kontoen som har abonnementet ditt. E-post, passord og eventuell tofaktor fylles inn på OpenAI/GitHub sin innloggingsside. Tabbar bruker deretter CLI-ens lagrede innlogging.\nTomme program-/profilfelt bruker automatisk oppsett." };
        content.Controls.Add(intro);
        codex = new ProviderEditor(false, Result.Codex, lifetime.Token);
        copilot = new ProviderEditor(true, Result.Copilot, lifetime.Token);
        content.Controls.Add(codex); content.Controls.Add(copilot);
        rotate.Checked = Result.AutoRotate;
        slideSeconds.Value = Result.SlideSeconds; refreshMinutes.Value = Result.RefreshMinutes;
        content.Controls.Add(rotate);
        var intervals = new FlowLayoutPanel { Width = 600, Height = 36, WrapContents = false };
        intervals.Controls.Add(new Label { Text = "Bytt hvert (sek)", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
        intervals.Controls.Add(slideSeconds);
        intervals.Controls.Add(new Label { Text = "Hent kvote hvert (min)", AutoSize = true, Padding = new Padding(16, 5, 0, 0) });
        intervals.Controls.Add(refreshMinutes); content.Controls.Add(intervals);
        var icons = new FlowLayoutPanel { Width = 600, Height = 34, WrapContents = false };
        icons.Controls.Add(new Label { Text = "Ikonhøyde i snarveilista (px)", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
        iconHeight.Value = Math.Clamp(shortcutIconHeight, 16, 128);
        icons.Controls.Add(iconHeight);
        icons.Controls.Add(new Label { Text = "16–128 px · kortene tilpasses", AutoSize = true, Padding = new Padding(8, 5, 0, 0) });
        content.Controls.Add(icons);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
        var save = new Button { Text = "Lagre", Width = 90 };
        var cancel = new Button { Text = "Avbryt", Width = 90, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) =>
        {
            var c = codex.Value; var p = copilot.Value;
            if (!ValidateProvider(c) || !ValidateProvider(p)) return;
            Result = new() { AutoRotate = rotate.Checked, SlideSeconds = (int)slideSeconds.Value, RefreshMinutes = (int)refreshMinutes.Value, Codex = c, Copilot = p };
            DialogResult = DialogResult.OK;
        };
        footer.Controls.Add(save); footer.Controls.Add(cancel);
        Controls.Add(content); Controls.Add(footer);
        AcceptButton = save; CancelButton = cancel;
        // Button text remains readable with the dark parent background.
        foreach (var button in new[] { save, cancel }) { button.UseVisualStyleBackColor = true; button.ForeColor = SystemColors.ControlText; }
    }

    private bool ValidateProvider(ProviderSettings value)
    {
        if (value.ExecutablePath.Length > 0 && (!Path.IsPathFullyQualified(value.ExecutablePath) || !File.Exists(value.ExecutablePath) || !value.ExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
        { MessageBox.Show(this, "Velg en eksisterende programfil (.exe) med full sti, eller la feltet stå tomt for automatisk søk.", Text); return false; }
        if (value.ProfileDirectory.Length > 0 && (!Path.IsPathFullyQualified(value.ProfileDirectory) || !Directory.Exists(value.ProfileDirectory)))
        { MessageBox.Show(this, "Velg en eksisterende profilmappe med full sti, eller la feltet stå tomt for standardprofilen.", Text); return false; }
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) lifetime.Cancel();
        base.Dispose(disposing);
    }

    private sealed class ProviderEditor : GroupBox
    {
        private readonly ToolTip tips = new() { AutoPopDelay = 20000 };
        private readonly TextBox executable = new() { Dock = DockStyle.Fill, PlaceholderText = "Automatisk" };
        private readonly TextBox profile = new() { Dock = DockStyle.Fill, PlaceholderText = "Standardprofil (valgfritt)" };
        internal ProviderSettings Value => new() { ExecutablePath = executable.Text.Trim(), ProfileDirectory = profile.Text.Trim() };

        internal ProviderEditor(bool isCopilot, ProviderSettings value, CancellationToken cancellation)
        {
            Text = isCopilot ? "GitHub Copilot" : "Codex / ChatGPT";
            ForeColor = Color.FromArgb(233, 239, 247);
            Width = 600; Height = 206;
            executable.Text = value.ExecutablePath; profile.Text = value.ProfileDirectory;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10, 6, 10, 8), ColumnCount = 3, RowCount = 5 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.Controls.Add(new Label { Text = "Programfil", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, 0, 0);
            layout.Controls.Add(executable, 1, 0);
            layout.Controls.Add(new Label { Text = "Profilmappe", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, 0, 1);
            layout.Controls.Add(profile, 1, 1);
            var pickExe = new Button { Text = "Velg …", Dock = DockStyle.Fill };
            pickExe.Click += (_, _) => { using var dialog = new OpenFileDialog { Filter = "Programfil|*.exe", FileName = isCopilot ? "copilot.exe" : "codex.exe" }; if (dialog.ShowDialog(this) == DialogResult.OK) executable.Text = dialog.FileName; };
            var pickProfile = new Button { Text = "Velg …", Dock = DockStyle.Fill };
            pickProfile.Click += (_, _) => { using var dialog = new FolderBrowserDialog { Description = "Velg CLI-profilen som inneholder innloggingen" }; if (dialog.ShowDialog(this) == DialogResult.OK) profile.Text = dialog.SelectedPath; };
            layout.Controls.Add(pickExe, 2, 0); layout.Controls.Add(pickProfile, 2, 1);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            var login = new Button { Text = "Logg inn / bytt konto", AutoSize = true };
            var test = new Button { Text = "Test forbindelse", AutoSize = true };
            actions.Controls.Add(login); actions.Controls.Add(test);
            layout.Controls.Add(actions, 0, 2); layout.SetColumnSpan(actions, 3);
            var hint = new Label { Text = isCopilot ? "Viser abonnementets premiumkvote." : "Viser tidsvinduet med minst kvote igjen. Bruk ChatGPT-innlogging.", Dock = DockStyle.Fill };
            layout.Controls.Add(hint, 0, 3); layout.SetColumnSpan(hint, 3);
            var status = new Label { Text = "Trykk Test forbindelse for å hente kvoten.", Dock = DockStyle.Fill, AutoEllipsis = true };
            layout.Controls.Add(status, 0, 4); layout.SetColumnSpan(status, 3);
            login.Click += (_, _) =>
            {
                try
                {
                    using var process = Process.Start(QuotaReader.LoginStartInfo(isCopilot, Value));
                    status.Text = "Fullfør innloggingen i nettleseren/innloggingsvinduet. Trykk deretter Test forbindelse.";
                    tips.SetToolTip(status, status.Text);
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
                { status.Text = "Kunne ikke starte innlogging. Kontroller programfil og profilmappe."; }
            };
            test.Click += async (_, _) =>
            {
                test.Enabled = false; login.Enabled = false; executable.Enabled = false; profile.Enabled = false; pickExe.Enabled = false; pickProfile.Enabled = false;
                status.Text = "Henter abonnementskvote …";
                var config = Value;
                try
                {
                    var result = await Task.Run(() => QuotaReader.ReadAsync(isCopilot, cancellation, config));
                    if (IsDisposed || cancellation.IsCancellationRequested) return;
                    status.Text = result.Unlimited ? "Tilkoblet · Ubegrenset kvote" : result.Remaining is { } left ? $"Tilkoblet · {left:0.#}% igjen ({result.Period})" : result.Detail ?? result.Period;
                    tips.SetToolTip(status, status.Text);
                }
                finally
                {
                    if (!IsDisposed) { test.Enabled = true; login.Enabled = true; executable.Enabled = true; profile.Enabled = true; pickExe.Enabled = true; pickProfile.Enabled = true; }
                }
            };
            foreach (var button in new[] { pickExe, pickProfile, login, test }) { button.ForeColor = SystemColors.ControlText; button.UseVisualStyleBackColor = true; }
            Controls.Add(layout);
        }
        protected override void Dispose(bool disposing) { if (disposing) tips.Dispose(); base.Dispose(disposing); }
    }
}
