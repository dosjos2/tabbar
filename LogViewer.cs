using System.Diagnostics;
using System.Text;

namespace Tabbar;

internal sealed class LogViewer : Form
{
    private readonly string logPath;
    private readonly RichTextBox text = new() { Dock = DockStyle.Fill, ReadOnly = true, WordWrap = false, DetectUrls = false, BackColor = Color.FromArgb(20, 26, 36), ForeColor = Color.FromArgb(233, 239, 247), Font = new Font("Consolas", 10), BorderStyle = BorderStyle.None };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    internal LogViewer(string name, string logPath)
    {
        this.logPath = logPath;
        Text = "Logg – " + name;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(860, 540);
        MinimumSize = new Size(450, 250);
        var folder = new Button { Text = "Åpne loggmappe", Dock = DockStyle.Bottom, Height = 34 };
        folder.Click += (_, _) => { try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + Path.GetDirectoryName(logPath) + "\"") { UseShellExecute = true }); } catch (Exception e) { MessageBox.Show(this, e.Message); } };
        Controls.Add(text);
        Controls.Add(folder);
        timer.Tick += (_, _) => RefreshLog();
        Shown += (_, _) => { RefreshLog(); timer.Start(); };
    }
    internal static string ReadTail(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var trimmed = file.Length > 256 * 1024;
        if (trimmed) file.Seek(-256 * 1024, SeekOrigin.End);
        using var reader = new StreamReader(file, Encoding.UTF8, true);
        if (trimmed) reader.ReadLine();
        return (trimmed ? "[Viser slutten av loggen. Hele filen ligger i loggmappen.]\r\n\r\n" : "") + reader.ReadToEnd();
    }
    private void RefreshLog()
    {
        string content;
        try { content = ReadTail(logPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { content = "Kan ikke lese loggen ennå:\n" + e.Message; }
        if (content == text.Text) return;
        var selection = text.SelectionStart;
        var length = text.SelectionLength;
        var follow = selection + length >= text.TextLength;
        text.Text = content;
        text.Select(follow ? text.TextLength : Math.Min(selection, text.TextLength), follow ? 0 : Math.Min(length, Math.Max(0, text.TextLength - selection)));
        if (follow) text.ScrollToCaret();
    }
    protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
}
