using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Tabbar;

internal readonly record struct MemoryReading(ulong TotalBytes, ulong AvailableBytes)
{
    internal ulong UsedBytes => TotalBytes - Math.Min(AvailableBytes, TotalBytes);
    internal double Percent => TotalBytes == 0 ? 0 : 100.0 * UsedBytes / TotalBytes;
    internal string AmountText => $"{UsedBytes / 1073741824.0:0.0} / {TotalBytes / 1073741824.0:0.0} GB";

    internal static MemoryReading? Read()
    {
        var status = new Native.MemoryStatus { Size = (uint)Marshal.SizeOf<Native.MemoryStatus>() };
        return Native.GlobalMemoryStatusEx(ref status) && status.TotalPhysical > 0
            ? new MemoryReading(status.TotalPhysical, status.AvailablePhysical) : null;
    }
}

internal sealed class MemoryGauge : Control
{
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly Font titleFont = new("Segoe UI", 8, FontStyle.Bold);
    private readonly Font valueFont = new("Segoe UI", 19, FontStyle.Bold);
    private readonly Font amountFont = new("Segoe UI", 8);
    private MemoryReading? reading;
    internal int RefreshCount { get; private set; }

    internal MemoryGauge()
    {
        Height = 142;
        Dock = DockStyle.Bottom;
        BackColor = Color.FromArgb(20, 26, 36);
        ForeColor = Color.FromArgb(233, 239, 247);
        AccessibleName = "Samlet RAM-bruk";
        AccessibleRole = AccessibleRole.ProgressBar;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        timer.Tick += (_, _) => RefreshReading();
    }

    private void RefreshReading()
    {
        reading = MemoryReading.Read();
        RefreshCount++;
        AccessibleDescription = reading is { } value
            ? $"RAM: {value.Percent:0} prosent, {value.AmountText} i bruk"
            : "RAM-bruk er ikke tilgjengelig";
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RefreshReading();
        timer.Start();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        timer.Stop();
        base.OnHandleDestroyed(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var divider = new Pen(Color.FromArgb(48, 64, 83));
        g.DrawLine(divider, 10, 0, Width - 10, 0);
        DrawText(g, "RAM", titleFont, Color.FromArgb(150, 162, 180), new Rectangle(0, 10, Width, 20));
        var diameter = Math.Max(30, Math.Min(106, Width - 30));
        var arc = new RectangleF((Width - diameter) / 2f, 36, diameter, diameter);
        using var track = new Pen(Color.FromArgb(42, 56, 73), 8) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(track, arc, 180, 180);
        if (reading is { } value)
        {
            var color = value.Percent >= 90 ? Color.FromArgb(242, 112, 112)
                : value.Percent >= 75 ? Color.FromArgb(238, 188, 89) : Color.FromArgb(82, 190, 180);
            using var fill = new Pen(color, 8) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            if (value.Percent > 0) g.DrawArc(fill, arc, 180, (float)(180 * value.Percent / 100));
            DrawText(g, $"{value.Percent:0}%", valueFont, ForeColor, new Rectangle(0, 66, Width, 32));
            DrawText(g, value.AmountText, amountFont, ForeColor, new Rectangle(0, 105, Width, 18));
            DrawText(g, "i bruk", amountFont, Color.FromArgb(150, 162, 180), new Rectangle(0, 122, Width, 18));
        }
        else DrawText(g, "Ikke tilgjengelig", amountFont, ForeColor, new Rectangle(0, 92, Width, 30));
    }

    private static void DrawText(Graphics graphics, string text, Font font, Color color, Rectangle bounds) =>
        TextRenderer.DrawText(graphics, text, font, bounds, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); titleFont.Dispose(); valueFont.Dispose(); amountFont.Dispose(); }
        base.Dispose(disposing);
    }
}
