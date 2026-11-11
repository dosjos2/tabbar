using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Tabbar;

internal sealed record ProcessMemory(int Id, string Name, long Bytes, long? StartedAt)
{
    internal string Amount => Bytes >= 1073741824 ? $"{Bytes / 1073741824.0:0.00} GB" : $"{Bytes / 1048576.0:0} MB";
}

internal static class ProcessMemoryReader
{
    internal static ProcessMemory[] ReadTop()
    {
        var readings = new List<ProcessMemory>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var bytes = process.WorkingSet64;
                    if (bytes <= 0) continue;
                    long? started = null;
                    try { started = process.StartTime.ToFileTimeUtc(); }
                    catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException) { }
                    readings.Add(new(process.Id, process.ProcessName, bytes, started));
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        return readings.OrderByDescending(p => p.Bytes).ThenBy(p => p.Id).Take(3).ToArray();
    }

    internal static void Terminate(ProcessMemory target)
    {
        if (target.Id <= 4 || target.Id == Environment.ProcessId)
            throw new InvalidOperationException("Denne prosessen kan ikke avsluttes fra Tabbar.");
        if (target.StartedAt is null)
            throw new InvalidOperationException("Prosessens identitet kunne ikke bekreftes. Oppdater listen og prøv igjen.");
        // Keep one handle throughout identity verification and termination, so a reused PID cannot target a different process.
        using var handle = Native.OpenProcess(0x1001, false, target.Id); // QUERY_LIMITED_INFORMATION | TERMINATE
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!Native.GetProcessTimes(handle, out var created, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (created != target.StartedAt.Value) throw new InvalidOperationException("Prosessen er avsluttet eller erstattet. Oppdater listen og prøv igjen.");
        if (!Native.IsProcessCritical(handle, out var critical)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (critical) throw new InvalidOperationException("Kritiske Windows-prosesser kan ikke avsluttes fra Tabbar.");
        if (!Native.TerminateProcess(handle, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}

internal sealed class TopMemoryProcesses : UserControl
{
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 3000 };
    private readonly ToolTip tips = new();
    private readonly ContextMenuStrip menu = new();
    private readonly Label[] rows = new Label[3];
    private ProcessMemory? selected;
    private bool refreshing, terminating;
    internal int RefreshCount { get; private set; }
    internal IReadOnlyList<ProcessMemory> Readings { get; private set; } = [];

    internal TopMemoryProcesses()
    {
        Height = 166;
        Dock = DockStyle.Bottom;
        BackColor = Color.FromArgb(20, 26, 36);
        ForeColor = Color.FromArgb(233, 239, 247);
        AccessibleName = "De tre prosessene som bruker mest RAM";
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10, 6, 10, 6), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        for (var i = 0; i < 3; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "TOPP 3 RAM", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(150, 162, 180), TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i] = new Label { Text = "Leser …", Dock = DockStyle.Fill, AutoEllipsis = true, UseMnemonic = false,
                TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty, ContextMenuStrip = menu, TabStop = true };
            layout.Controls.Add(rows[i], 0, i + 1);
        }
        var hint = new Label { Text = "Høyreklikk", Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Color.FromArgb(150, 162, 180) };
        layout.Controls.Add(hint, 0, 4);
        tips.SetToolTip(hint, "Minne i fysisk RAM (working set), per prosess. Høyreklikk for å avslutte en prosess.");
        Controls.Add(layout);
        var kill = menu.Items.Add("Avslutt prosess …");
        menu.Opening += (_, e) =>
        {
            selected = menu.SourceControl?.Tag as ProcessMemory;
            e.Cancel = selected is null;
            kill.Enabled = selected is { StartedAt: not null, Id: > 4 } && selected.Id != Environment.ProcessId;
        };
        kill.Click += async (_, _) => await TerminateSelectedAsync();
        timer.Tick += async (_, _) => await RefreshProcessesAsync();
    }

    internal async Task RefreshProcessesAsync()
    {
        if (refreshing || terminating || menu.Visible || IsDisposed || !IsHandleCreated) return;
        refreshing = true;
        try
        {
            var readings = await Task.Run(ProcessMemoryReader.ReadTop);
            if (IsDisposed || !IsHandleCreated || menu.Visible || terminating) return;
            Readings = readings;
            RefreshCount++;
            for (var i = 0; i < rows.Length; i++)
            {
                var entry = i < readings.Length ? readings[i] : null;
                rows[i].Tag = entry;
                rows[i].Text = entry is null ? "Ikke tilgjengelig" : $"{i + 1}. {entry.Name}\n{entry.Amount}";
                rows[i].AccessibleName = entry is null ? "Ingen prosess" : $"{entry.Name}, {entry.Amount}, PID {entry.Id}";
                tips.SetToolTip(rows[i], entry is null ? "" : $"{entry.Name}\nPID: {entry.Id}\nRAM: {entry.Amount} (working set)\nHøyreklikk for å avslutte");
            }
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            if (!IsDisposed)
                foreach (var row in rows) { row.Tag = null; row.Text = "Ikke tilgjengelig"; }
        }
        finally { refreshing = false; }
    }

    private async Task TerminateSelectedAsync()
    {
        var target = selected;
        if (target is null || terminating) return;
        terminating = true;
        try
        {
            if (MessageBox.Show(this, $"Avslutte {target.Name} (PID {target.Id})?\nUlagret arbeid kan gå tapt.", "Avslutt prosess",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            await Task.Run(() => ProcessMemoryReader.Terminate(target));
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            if (!IsDisposed) MessageBox.Show(this, "Kunne ikke avslutte prosessen:\n" + e.Message, "Tabbar", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally
        {
            terminating = false;
            if (!IsDisposed) await RefreshProcessesAsync();
        }
    }

    protected override async void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        timer.Start();
        await RefreshProcessesAsync();
    }
    protected override void OnHandleDestroyed(EventArgs e) { timer.Stop(); base.OnHandleDestroyed(e); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); menu.Dispose(); tips.Dispose(); }
        base.Dispose(disposing);
    }
}
