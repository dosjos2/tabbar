namespace Tabbar;

internal sealed class GaugeSlideshow : UserControl
{
    private readonly System.Windows.Forms.Timer timer = new();
    private readonly Control[] slides;
    private readonly Button previous;
    private readonly Button next;
    private readonly Label position = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.FromArgb(150, 162, 180) };
    internal Button PreviousButton => previous;
    internal Button NextButton => next;
    private bool autoRotate;
    internal int SelectedIndex { get; private set; }
    internal MemoryGauge Memory { get; } = new();
    internal QuotaGauge Codex { get; }
    internal QuotaGauge Copilot { get; }

    internal GaugeSlideshow(GaugeSettings settings, bool fetchQuota = true)
    {
        Dock = DockStyle.Bottom;
        Height = 170;
        BackColor = Color.FromArgb(20, 26, 36);
        AccessibleName = "RAM og abonnementskvoter";
        Codex = new(false, fetchQuota); Copilot = new(true, fetchQuota);
        slides = [Memory, Codex, Copilot];
        var host = new Panel { Dock = DockStyle.Fill };
        foreach (var slide in slides) { slide.Dock = DockStyle.Fill; host.Controls.Add(slide); }
        previous = Arrow("◀", "Forrige måler", -1);
        next = Arrow("▶", "Neste måler", 1);
        previous.Dock = DockStyle.Left; next.Dock = DockStyle.Right;
        var navigation = new Panel { Dock = DockStyle.Bottom, Height = 28, Padding = new Padding(4, 0, 4, 0) };
        navigation.Controls.Add(position); navigation.Controls.Add(previous); navigation.Controls.Add(next);
        Controls.Add(host); Controls.Add(navigation);
        timer.Tick += (_, _) => { if (!ClientRectangle.Contains(PointToClient(MousePosition))) Advance(1, false); };
        Configure(settings);
        ShowSlide();
    }

    private Button Arrow(string text, string name, int direction)
    {
        var button = new Button { Text = text, AccessibleName = name, FlatStyle = FlatStyle.Flat, Size = new Size(24, 26),
            Font = new Font("Segoe UI", 9), ForeColor = Color.FromArgb(170, 192, 210), BackColor = BackColor, Cursor = Cursors.Hand };
        button.FlatAppearance.BorderSize = 0;
        button.Click += (_, _) => Advance(direction);
        return button;
    }

    internal void Advance(int direction, bool manual = true)
    {
        SelectedIndex = ((SelectedIndex + direction) % slides.Length + slides.Length) % slides.Length;
        ShowSlide();
        if (manual) { timer.Stop(); if (autoRotate && IsHandleCreated) timer.Start(); }
    }

    private void ShowSlide()
    {
        for (var i = 0; i < slides.Length; i++) slides[i].Visible = i == SelectedIndex;
        AccessibleDescription = new[] { "RAM", "Codex", "Copilot" }[SelectedIndex] + $", {SelectedIndex + 1} av 3";
        position.Text = $"{SelectedIndex + 1} / 3";
    }

    internal void Configure(GaugeSettings settings)
    {
        autoRotate = settings.AutoRotate;
        timer.Interval = Math.Clamp(settings.SlideSeconds, 3, 120) * 1000;
        Codex.Configure(settings.Codex, settings.RefreshMinutes);
        Copilot.Configure(settings.Copilot, settings.RefreshMinutes);
        timer.Stop(); if (autoRotate && IsHandleCreated) timer.Start();
    }

    internal async Task RefreshQuotasAsync() => await Task.WhenAll(Codex.RefreshQuotaAsync(), Copilot.RefreshQuotaAsync());
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); if (autoRotate) timer.Start(); }
    protected override void OnHandleDestroyed(EventArgs e) { timer.Stop(); base.OnHandleDestroyed(e); }
    protected override void Dispose(bool disposing) { if (disposing) { timer.Dispose(); previous.Font.Dispose(); next.Font.Dispose(); } base.Dispose(disposing); }
}
