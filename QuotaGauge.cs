using System.Drawing.Drawing2D;

namespace Tabbar;

internal sealed class QuotaGauge : Control
{
    private readonly bool copilot;
    private readonly bool autoRefresh;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 300000 };
    private readonly CancellationTokenSource lifetime = new();
    private readonly ToolTip tips = new() { AutoPopDelay = 20000 };
    private readonly Font titleFont = new("Segoe UI", 8, FontStyle.Bold);
    private readonly Font valueFont = new("Segoe UI", 18, FontStyle.Bold);
    private readonly Font detailFont = new("Segoe UI", 8);
    private QuotaReading reading = new(null, "Leser kvote …");
    private bool refreshing;
    private ProviderSettings settings = new();
    private int refreshMinutes = 5;
    private int configurationVersion;
    internal int RefreshCount { get; private set; }
    internal QuotaReading Reading => reading;

    internal QuotaGauge(bool copilot, bool autoRefresh = true)
    {
        this.copilot = copilot;
        this.autoRefresh = autoRefresh;
        Dock = DockStyle.Bottom;
        Height = 128;
        BackColor = Color.FromArgb(20, 26, 36);
        ForeColor = Color.FromArgb(233, 239, 247);
        AccessibleName = (copilot ? "Copilot" : "Codex") + " gjenstående abonnementskvote";
        AccessibleRole = AccessibleRole.ProgressBar;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        timer.Tick += async (_, _) => await RefreshQuotaAsync();
        Cursor = Cursors.Hand;
        Click += async (_, _) => await RefreshQuotaAsync();
    }

    internal async Task RefreshQuotaAsync()
    {
        if (refreshing || IsDisposed || !IsHandleCreated) return;
        refreshing = true;
        var version = configurationVersion;
        var configuration = settings with { };
        try
        {
            var value = await Task.Run(() => QuotaReader.ReadAsync(copilot, lifetime.Token, configuration));
            if (IsDisposed || !IsHandleCreated || version != configurationVersion) return;
            reading = value;
            RefreshCount++;
            var description = value.Unlimited ? "Ubegrenset" : value.Remaining is { } left ? $"{left:0.#}% igjen ({value.Period})" : value.Period;
            AccessibleDescription = description;
            tips.SetToolTip(this, AccessibleName + "\n" + description + "\n" + value.Detail
                + (value.ResetsAt is { } reset ? $"\nNullstilles: {reset.LocalDateTime:g}" : "")
                + (value.ResetNote is { } note ? "\n" + note : "")
                + $"\nSist kontrollert: {DateTime.Now:T}\nOppdateres hvert {refreshMinutes}. minutt. Klikk for å oppdatere.");
            Invalidate();
        }
        finally
        {
            refreshing = false;
            if (!IsDisposed && IsHandleCreated && version != configurationVersion) await RefreshQuotaAsync();
        }
    }

    internal void Configure(ProviderSettings value, int minutes)
    {
        settings = value with { };
        refreshMinutes = Math.Clamp(minutes, 1, 60);
        timer.Interval = refreshMinutes * 60000;
        configurationVersion++;
        reading = new(null, "Leser kvote …");
        tips.SetToolTip(this, null);
        Invalidate();
    }

    protected override async void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!autoRefresh) return;
        timer.Start();
        await RefreshQuotaAsync();
    }
    protected override void OnHandleDestroyed(EventArgs e) { timer.Stop(); base.OnHandleDestroyed(e); }

    internal void SetPreview(QuotaReading value) { reading = value; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var divider = new Pen(Color.FromArgb(48, 64, 83));
        g.DrawLine(divider, 10, 0, Width - 10, 0);
        Draw(g, copilot ? "COPILOT" : "CODEX", titleFont, Color.FromArgb(150, 162, 180), 6, 20);
        var diameter = Math.Max(30, Math.Min(92, Width - 30));
        var arc = new RectangleF((Width - diameter) / 2f, 32, diameter, diameter);
        using var track = new Pen(Color.FromArgb(42, 56, 73), 7) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(track, arc, 180, 180);
        var left = reading.Unlimited ? 100 : reading.Remaining;
        if (left is > 0)
        {
            var color = left <= 10 ? Color.FromArgb(242, 112, 112) : left <= 25 ? Color.FromArgb(238, 188, 89)
                : copilot ? Color.FromArgb(161, 150, 238) : Color.FromArgb(82, 190, 180);
            using var fill = new Pen(color, 7) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(fill, arc, 180, (float)(180 * left / 100));
        }
        Draw(g, reading.Unlimited ? "∞" : left is { } number ? $"{number:0}%" : "—", valueFont, ForeColor, 57, 32);
        Draw(g, reading.Unlimited ? "Ubegrenset" : left is not null ? "igjen · " + reading.Period : reading.Period, detailFont, ForeColor, 91, 18);
        Draw(g, reading.ResetsAt is { } reset ? $"Reset {reset.LocalDateTime:dd.MM HH:mm}" : "Abonnementskvote", detailFont, Color.FromArgb(150, 162, 180), 109, 17);
    }

    private void Draw(Graphics g, string text, Font font, Color color, int top, int height) => TextRenderer.DrawText(g, text, font,
        new Rectangle(2, top, Math.Max(1, Width - 4), height), color,
        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

    protected override void Dispose(bool disposing)
    {
        if (disposing) { lifetime.Cancel(); timer.Dispose(); tips.Dispose(); titleFont.Dispose(); valueFont.Dispose(); detailFont.Dispose(); }
        base.Dispose(disposing);
    }
}
