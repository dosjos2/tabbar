using System.Diagnostics;

namespace Tabbar;

internal static class SelfTest
{
    internal static void Run()
    {
        var report = new List<string>();
        var temp = Path.Combine(Path.GetTempPath(), "Tabbar-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            void Check(bool condition, string name)
            {
                if (!condition) throw new InvalidOperationException(name);
                report.Add("PASS: " + name);
                File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "self-test-results.txt"), report);
            }
            var store = new SettingsStore(temp);
            var memory = MemoryReading.Read();
            Check(memory is { TotalBytes: > 0 } && memory.Value.AvailableBytes <= memory.Value.TotalBytes && memory.Value.Percent is >= 0 and <= 100, "System physical RAM reading is available and valid");
            Check(store.Load().Width == 150, "Default width is 150 physical pixels");
            var script = Path.Combine(temp, "test space & (special) %PATH% !.bat");
            File.WriteAllText(script, "@echo off\r\ncd > \"%~dp0working-directory.txt\"\r\n");
            var entry = new ScriptEntry { Name = "Test", Path = script };
            var settings = new Settings { Scripts = [entry] };
            store.Save(settings);
            var loaded = store.Load();
            Check(loaded.Scripts.Single().Path == script, "Settings round trip preserves script paths");
            void RunScript(bool scriptFolder)
            {
                entry.UseScriptFolder = scriptFolder;
                var elevated = ScriptRunner.CreateLaunchStartInfo(entry, false);
                Check(elevated.UseShellExecute && elevated.Verb == "runas", "Script launch requests administrator elevation through Windows UAC");
                var start = ScriptRunner.ReadLaunchRequest(elevated.Arguments["--run-script ".Length..]);
                start.CreateNoWindow = true;
                using var process = Process.Start(start)!;
                if (!process.WaitForExit(10000)) { process.Kill(true); throw new Exception("Script timeout"); }
                Check(process.ExitCode == 0, "Script exits successfully with spaces, &, parentheses, %, ! in path");
                var actual = File.ReadAllText(Path.Combine(temp, "working-directory.txt")).Trim();
                Check(actual.Equals(scriptFolder ? temp : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), StringComparison.OrdinalIgnoreCase), scriptFolder ? "Script-folder working directory" : "Desktop working directory");
            }
            RunScript(false);
            RunScript(true);
            var keepOpen = ScriptRunner.CreateLaunchStartInfo(entry, true);
            Check(ScriptRunner.ReadLaunchRequest(keepOpen.Arguments["--run-script ".Length..]).Arguments.Contains("/k"), "Elevated launch preserves keep-console-open setting");
            TestExecution(temp, Check);
            TestShortcuts(temp, Check);
            TestProcessMemory(Check);
            report.Add("SKIP: Actual UAC consent and elevated token require manual verification in a normal Windows session.");
            var originalArea = Screen.PrimaryScreen!.WorkingArea;
            var hasShell = Native.GetShellWindow() != IntPtr.Zero;
            if (!hasShell) report.Add("SKIP: No Explorer shell is available on this test desktop; AppBar/maximize integration requires a normal Windows desktop session.");
            Check(true, "Starting appbar UI test");
            using (var form = new ToolbarForm(store, loaded, hasShell))
            {
                if (!hasShell) form.SetBounds(Screen.PrimaryScreen!.Bounds.Right - 150, 0, 150, originalArea.Height);
                Check(true, "Toolbar constructed");
                form.Show();
                Check(true, "Toolbar shown");
                Pump();
                Check(form.Width == 150, "Appbar has requested width");
                Check(form.Right == Screen.PrimaryScreen!.Bounds.Right, "Appbar docks to right edge");
                var slideshow = form.Controls.OfType<GaugeSlideshow>().Single();
                var gauge = slideshow.Memory;
                var topMemory = form.Controls.OfType<TopMemoryProcesses>().Single();
                Check(topMemory.Bottom == form.ClientSize.Height && slideshow.Bottom == topMemory.Top && gauge.Width > 0, "Top RAM processes are anchored directly below the gauge slideshow at the bottom of the toolbar");
                var processWait = Stopwatch.StartNew();
                while (topMemory.RefreshCount == 0 && processWait.ElapsedMilliseconds < 5000) Pump(50);
                Check(topMemory.Readings.Count is > 0 and <= 3, "Top RAM process list loads automatically without blocking the toolbar");
                var refreshCount = gauge.RefreshCount;
                Pump(1200);
                Check(gauge.RefreshCount > refreshCount, "RAM gauge refreshes automatically while toolbar is open");
                var processRefreshCount = topMemory.RefreshCount;
                Pump(3200);
                Check(topMemory.RefreshCount > processRefreshCount, "Top RAM process list refreshes automatically");
                var processRow = topMemory.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<Label>().First(l => l.Tag is ProcessMemory);
                var selectedProcess = processRow.Tag;
                var processMenu = processRow.ContextMenuStrip!;
                processMenu.Show(processRow, new Point(0, processRow.Height)); Pump(100);
                var countWhileOpen = topMemory.RefreshCount;
                topMemory.RefreshProcessesAsync().GetAwaiter().GetResult();
                Check(processMenu.Visible && processMenu.Items[0].Text == "Avslutt prosess …"
                    && ReferenceEquals(processRow.Tag, selectedProcess) && topMemory.RefreshCount == countWhileOpen,
                    "Right-click menu exposes termination and freezes the selected process until the menu closes");
                processMenu.Close();
                foreach (var width in new[] { 95, 145, 295 })
                {
                    using var sample = new MemoryGauge { Width = width };
                    sample.CreateControl();
                    using var bitmap = new Bitmap(sample.Width, sample.Height);
                    sample.DrawToBitmap(bitmap, sample.ClientRectangle);
                    bitmap.Save(Path.Combine(AppContext.BaseDirectory, $"ram-gauge-{width}.png"));
                    using var processSample = new TopMemoryProcesses { Width = width };
                    processSample.CreateControl();
                    var sampleWait = Stopwatch.StartNew();
                    while (processSample.RefreshCount == 0 && sampleWait.ElapsedMilliseconds < 5000) Pump(50);
                    using var processBitmap = new Bitmap(processSample.Width, processSample.Height);
                    processSample.DrawToBitmap(processBitmap, processSample.ClientRectangle);
                    processBitmap.Save(Path.Combine(AppContext.BaseDirectory, $"top-ram-{width}.png"));
                }
                var area = Screen.PrimaryScreen!.WorkingArea;
                if (hasShell) Check(area.Right <= form.Left, "Windows work area excludes toolbar");
                if (hasShell) using (var other = new Form { Text = "Tabbar maximize test", StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(originalArea.Left + 30, originalArea.Top + 30, 400, 300) })
                {
                    other.Show(); other.WindowState = FormWindowState.Maximized; Pump();
                    // Normal Win32 maximized window frames have an invisible resize border.
                    Check(other.Right <= form.Left + 16, "Maximized application respects toolbar reservation");
                    other.Close();
                }
                loaded.Width = 220;
                form.PositionBar(); Pump();
                if (hasShell) Check(form.Width == 220 && Screen.PrimaryScreen!.WorkingArea.Right <= form.Left, "Resizing updates desktop reservation");
                form.AddFiles([script, script, Path.Combine(temp, "missing.bat")]);
                Check(loaded.Scripts.Count == 1, "Duplicate and missing files are ignored");
                loaded.Scripts[0].Name = "Mitt testscript";
                loaded.Scripts[0].RunAsAdministrator = false;
                loaded.Scripts[0].IconPath = Path.Combine(temp, "custom-icon.png");
                using (var properties = new ScriptPropertiesDialog(loaded.Scripts[0], Path.Combine(temp, "icons")))
                {
                    properties.Show(); Pump();
                    using var image = new Bitmap(properties.Width, properties.Height);
                    properties.DrawToBitmap(image, new Rectangle(0, 0, properties.Width, properties.Height));
                    image.Save(Path.Combine(AppContext.BaseDirectory, "properties-preview.png"));
                    properties.Close();
                }
                loaded.KeepConsole = false;
                // This harness pumps messages without Application.Run. Restore
                // the UI context after closing the preview windows above.
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                var uiRun = form.RunScriptAsync(loaded.Scripts[0]);
                var uiWait = Stopwatch.StartNew();
                while (!uiRun.IsCompleted && uiWait.ElapsedMilliseconds < 15000) Pump(50);
                Check(uiRun.IsCompleted, "Toolbar completes an actual script launch without blocking the interface");
                uiRun.GetAwaiter().GetResult();
                Check(loaded.Scripts[0].LastExitCode == 0 && File.Exists(loaded.Scripts[0].LastLogPath), "Toolbar persists successful run status and log location");
                Check(form.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().Single().Text.Contains("Klar"), "Toolbar card returns to ready after the script ends");
                loaded.Width = 150;
                form.PositionBar(); Pump();
                using var preview = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(preview, form.ClientRectangle);
                preview.Save(Path.Combine(AppContext.BaseDirectory, "preview.png"));
                form.Close();
            }
            Pump();
            Check(Screen.PrimaryScreen!.WorkingArea == originalArea, "Closing restores original desktop work area");
            report.Add(hasShell ? "All tests passed." : "All available tests passed; desktop integration tests skipped (no Explorer shell).");
            Environment.ExitCode = 0;
        }
        catch (Exception e) { report.Add("FAIL: " + e); Environment.ExitCode = 1; }
        finally
        {
            // Helpers publish completion just before exiting; allow their final
            // file/working-directory handles to close before removing fixtures.
            var cleanup = Stopwatch.StartNew();
            while (true)
            {
                try { Directory.Delete(temp, true); break; }
                catch (IOException) when (cleanup.ElapsedMilliseconds < 3000) { Thread.Sleep(100); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    report.Add("WARN: Temporary test files could not be fully removed: " + e.Message);
                    break;
                }
            }
            SynchronizationContext.SetSynchronizationContext(null);
            File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "self-test-results.txt"), report);
        }
    }

    private static void TestExecution(string temp, Action<bool, string> check)
    {
        var path = Path.Combine(temp, "logged space & %PATH% ! (test).cmd");
        File.WriteAllText(path, "@echo off\r\necho STDOUT_MARKER\r\necho STDERR_MARKER 1>&2\r\nfor /l %%i in (1,1,1000) do (echo output-%%i & echo error-%%i 1>&2)\r\nexit /b 7\r\n");
        var entry = new ScriptEntry { Path = path, Name = "Eget navn", RunAsAdministrator = false, UseScriptFolder = true };
        var start = ScriptRunner.CreateLaunchStartInfo(entry, true, "log", "result");
        check(!start.UseShellExecute && string.IsNullOrEmpty(start.Verb), "Per-script administrator option can disable UAC");
        var request = ScriptRunner.DecodeRequest(start.Arguments["--run-script ".Length..]);
        check(request.KeepConsole && request.WorkingDirectory == temp && !ScriptRunner.ReadLaunchRequest(start.Arguments["--run-script ".Length..]).Arguments.Contains("/k"), "Logged execution retains console preference while tracking actual script completion");
        var execution = new ScriptExecution(Path.Combine(temp, "logs"));
        var outcome = execution.RunAsync(entry, false, showConsole: false).WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        var log = File.ReadAllText(outcome.LogPath);
        check(outcome.Result.ExitCode == 7 && log.Contains("STDOUT_MARKER") && log.Contains("STDERR_MARKER") && log.Contains("output-1000") && log.Contains("error-1000"), "Real helper captures stdout/stderr without deadlock and reports nonzero exit code");
        check(!execution.IsRunning(path), "Failed script releases duplicate-run lock");
        check(LogViewer.ReadTail(outcome.LogPath).Contains("Avslutningskode: 7"), "Log viewer can read completed execution output");
        entry.LastRunAt = DateTimeOffset.Now;
        entry.LastLogPath = outcome.LogPath;
        var settings = new Settings { Scripts = [entry] };
        check(SettingsStore.RefreshResults(settings) && entry.LastExitCode == 7, "Completed results recover after toolbar restart");
        using (var image = new Bitmap(48, 32))
        {
            using var graphics = Graphics.FromImage(image);
            graphics.Clear(Color.CornflowerBlue);
            entry.IconPath = Path.Combine(temp, "custom-icon.png");
            image.Save(entry.IconPath);
        }
        var store = new SettingsStore(Path.Combine(temp, "custom-settings"));
        store.Save(settings);
        var saved = store.Load().Scripts.Single();
        check(saved.Name == "Eget navn" && !saved.RunAsAdministrator && saved.IconPath == entry.IconPath && saved.LastExitCode == 7, "Custom name, icon, administrator setting and last result persist");
        using (var icon = ScriptIcons.Load(saved)) check(icon.Width == 32 && icon.Height == 32, "Custom image renders as script icon");
        File.WriteAllText(store.FilePath, "{\"Scripts\":[{\"Path\":\"test.bat\",\"Name\":\"Old\"}]}");
        check(store.Load().Scripts.Single().RunAsAdministrator, "Existing settings retain administrator launch by default");
        File.WriteAllText(path, "@echo off\r\necho RUNNING_MARKER\r\nping 127.0.0.1 -n 4 >nul\r\nexit /b 0\r\n");
        string? liveLog = null;
        var running = execution.RunAsync(entry, false, logPath => liveLog = logPath, showConsole: false);
        check(execution.IsRunning(path), "Running script is marked active immediately");
        var wait = Stopwatch.StartNew();
        while (wait.ElapsedMilliseconds < 5000 && (liveLog is null || !LogViewer.ReadTail(liveLog).Contains("RUNNING_MARKER"))) Thread.Sleep(25);
        check(liveLog is not null && LogViewer.ReadTail(liveLog).Contains("RUNNING_MARKER"), "Log output is readable while script is running");
        check(new ScriptExecution(Path.Combine(temp, "logs")).IsRunning(path), "A restarted toolbar detects an existing running helper");
        var rejected = false;
        try { execution.RunAsync(entry, false, showConsole: false).GetAwaiter().GetResult(); }
        catch (InvalidOperationException) { rejected = true; }
        check(rejected, "Duplicate script launch is rejected");
        var success = running.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        check(success.Result.ExitCode == 0 && !execution.IsRunning(path), "Successful completion reports zero and releases run lock");
        var background = Path.Combine(temp, "background.cmd");
        File.WriteAllText(background, "@echo off\r\nping 127.0.0.1 -n 10 >nul\r\n");
        File.WriteAllText(path, "@echo off\r\nstart \"\" /b cmd /d /c background.cmd\r\necho MAIN_SCRIPT_FINISHED\r\nexit /b 0\r\n");
        var backgroundWait = Stopwatch.StartNew();
        var detached = execution.RunAsync(entry, false, showConsole: false).WaitAsync(TimeSpan.FromSeconds(7)).GetAwaiter().GetResult();
        check(detached.Result.ExitCode == 0 && backgroundWait.ElapsedMilliseconds < 7000 && File.ReadAllText(detached.LogPath).Contains("MAIN_SCRIPT_FINISHED"), "Detached background processes do not prevent reporting main script completion");
        var huge = Path.Combine(temp, "large.log");
        File.WriteAllText(huge, new string('x', 300000) + "\r\nTAIL_MARKER");
        var tail = LogViewer.ReadTail(huge);
        check(tail.Length < 262300 && tail.Contains("TAIL_MARKER"), "Large logs are bounded in the viewer without losing their tail");
        using var viewer = new LogViewer("Test", success.LogPath);
        viewer.Show(); Pump();
        using var preview = new Bitmap(viewer.Width, viewer.Height);
        viewer.DrawToBitmap(preview, new Rectangle(0, 0, viewer.Width, viewer.Height));
        preview.Save(Path.Combine(AppContext.BaseDirectory, "log-preview.png"));
        viewer.Close();
    }
    private static void TestShortcuts(string temp, Action<bool, string> check)
    {
        var folder = Path.Combine(temp, "Mappe med mellomrom & tegn");
        Directory.CreateDirectory(folder);
        var directory = ShortcutTargets.Create(folder)!;
        var website = ShortcutTargets.Create("https://example.com/Tools?q=a%26b#section")!;
        var program = ShortcutTargets.Create(Environment.ProcessPath!)!;
        var linkPath = Path.Combine(temp, "Windows shortcut.lnk");
        File.WriteAllText(linkPath, "Fixture only; never launched");
        var link = ShortcutTargets.Create(linkPath)!;
        check(directory.TargetKind == ShortcutKind.Folder && directory.Exists && !directory.CanElevate, "Folder shortcuts are classified and never elevated");
        check(website.TargetKind == ShortcutKind.Website && website.Exists && !website.CanElevate, "HTTPS shortcuts preserve query and fragment and never elevate");
        check(website.Path.EndsWith("/Tools?q=a%26b#section"), "Website target retains its complete address");
        check(!ShortcutTargets.IsWebsite("file:///C:/Windows") && !ShortcutTargets.IsWebsite("javascript:alert(1)")
            && !ShortcutTargets.IsWebsite("not a URL") && ShortcutTargets.IsWebsite("http://localhost:8080/test"), "Only absolute HTTP and HTTPS websites are accepted");
        check(ShortcutTargets.Create(Path.Combine(temp, "unknown.txt")) is null && ShortcutTargets.Create(Path.Combine(temp, "missing.exe")) is null, "Unsupported and missing files are rejected");
        check(program.TargetKind == ShortcutKind.Program && !program.RunAsAdministrator && program.UseScriptFolder, "Programs default to normal permissions and their own folder");
        var openProgram = ShortcutTargets.CreateOpenStartInfo(program);
        check(openProgram.UseShellExecute && openProgram.Verb == "" && openProgram.WorkingDirectory == Path.GetDirectoryName(program.Path), "Programs launch directly through Windows with the expected working directory");
        program.RunAsAdministrator = true;
        check(ShortcutTargets.CreateOpenStartInfo(program).Verb == "runas", "Programs support optional administrator elevation");
        check(ShortcutTargets.CreateOpenStartInfo(directory).FileName == folder && ShortcutTargets.CreateOpenStartInfo(website).FileName == website.Path
            && ShortcutTargets.CreateOpenStartInfo(link).UseShellExecute, "Folders, websites and Windows shortcuts use shell opening");
        check(!ShortcutTargets.SameTarget(website, ShortcutTargets.Create("https://example.com/tools?q=a%26b#section")!), "Website paths remain case-sensitive for duplicate detection");
        var script = Path.Combine(temp, "PowerShell space & (special) %PATH% !.ps1");
        File.WriteAllText(script, "[Console]::WriteLine('POWERSHELL_OUTPUT')\r\n[Console]::Error.WriteLine('POWERSHELL_ERROR')\r\n[Console]::WriteLine((Get-Location).Path)\r\nexit 7\r\n");
        var powershell = ShortcutTargets.Create(script)!;
        powershell.RunAsAdministrator = false;
        powershell.UseScriptFolder = true;
        var psStart = ScriptRunner.CreateStartInfo(powershell, false);
        check(psStart.ArgumentList.Last() == script && !psStart.ArgumentList.Contains("-ExecutionPolicy"), "PowerShell file paths are passed as one argument without overriding execution policy");
        var execution = new ScriptExecution(Path.Combine(temp, "shortcut-logs"));
        var outcome = execution.RunAsync(powershell, false, showConsole: false).WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
        var log = File.ReadAllText(outcome.LogPath);
        check(outcome.Result.ExitCode == 7 && log.Contains("POWERSHELL_OUTPUT") && log.Contains("POWERSHELL_ERROR") && log.Contains(temp), "PowerShell execution captures stdout, stderr, working directory and exit code");
        var store = new SettingsStore(Path.Combine(temp, "shortcut-settings"));
        var settings = new Settings();
        using (var form = new ToolbarForm(store, settings, reserveDesktop: false))
        {
            form.SetBounds(0, 0, 220, 800);
            form.Show();
            form.AddFiles([folder, folder + Path.DirectorySeparatorChar, website.Path, program.Path, linkPath, script]);
            check(settings.Scripts.Count == 5, "Toolbar accepts all shortcut types and rejects duplicate folders");
            var cards = form.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().ToArray();
            check(cards.Length == 5 && cards.Any(c => c.Text.Contains("Nettside")) && cards.Any(c => c.Text.Contains("Mappe")), "Toolbar displays folder and website cards without treating them as missing files");
            Pump();
            using (var toolbarPreview = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(toolbarPreview, form.ClientRectangle);
                toolbarPreview.Save(Path.Combine(AppContext.BaseDirectory, "shortcuts-preview.png"));
            }
            using var properties = new ScriptPropertiesDialog(website, Path.Combine(temp, "icons"));
            properties.Show(); Pump();
            var propertiesLayout = properties.Controls.OfType<TableLayoutPanel>().Single();
            check(propertiesLayout.Controls.OfType<Label>().Any(l => l.Text == website.Path && l.Visible && l.Height > 0)
                && propertiesLayout.Controls.OfType<CheckBox>().All(c => !c.Visible), "Website properties show the target and hide script-only options");
            using var preview = new Bitmap(properties.Width, properties.Height);
            properties.DrawToBitmap(preview, new Rectangle(Point.Empty, properties.Size));
            preview.Save(Path.Combine(AppContext.BaseDirectory, "website-properties-preview.png"));
            properties.Close();
            using var websiteDialog = new WebsiteDialog();
            websiteDialog.Show(); Pump();
            using var websitePreview = new Bitmap(websiteDialog.Width, websiteDialog.Height);
            websiteDialog.DrawToBitmap(websitePreview, new Rectangle(Point.Empty, websiteDialog.Size));
            websitePreview.Save(Path.Combine(AppContext.BaseDirectory, "website-add-preview.png"));
            websiteDialog.Close();
            form.Close();
        }
        var restored = store.Load();
        check(restored.Scripts.Count == 5 && restored.Scripts.Any(s => s.TargetKind == ShortcutKind.Website)
            && restored.Scripts.Any(s => s.TargetKind == ShortcutKind.Folder), "All shortcut types survive settings round trip");
        Directory.Delete(folder);
        check(!directory.Exists && directory.TargetKind == ShortcutKind.Folder, "Missing folders retain their shortcut type");
        check(new ScriptEntry { Path = "legacy.cmd" }.IsScript, "Legacy entries infer their script type without migration");
    }
    private static void TestProcessMemory(Action<bool, string> check)
    {
        var readings = ProcessMemoryReader.ReadTop();
        check(readings.Length is > 0 and <= 3 && readings.All(r => r.Bytes > 0)
            && readings.Zip(readings.Skip(1), (a, b) => a.Bytes >= b.Bytes).All(sorted => sorted), "Top RAM readings contain up to three processes ordered by physical memory usage");
        var rejectedSelf = false;
        try { ProcessMemoryReader.Terminate(new(Environment.ProcessId, "Tabbar", 1, 1)); }
        catch (InvalidOperationException) { rejectedSelf = true; }
        check(rejectedSelf, "Process termination cannot kill the toolbar itself");
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe")) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("127.0.0.1");
        start.ArgumentList.Add("-n");
        start.ArgumentList.Add("60");
        using var child = Process.Start(start)!;
        try
        {
            var target = new ProcessMemory(child.Id, child.ProcessName, child.WorkingSet64, child.StartTime.ToFileTimeUtc());
            var rejectedIdentity = false;
            try { ProcessMemoryReader.Terminate(target with { StartedAt = target.StartedAt + 1 }); }
            catch (InvalidOperationException) { rejectedIdentity = true; }
            check(rejectedIdentity && !child.HasExited, "Termination rejects a changed process identity without killing the process");
            var rejectedUnknown = false;
            try { ProcessMemoryReader.Terminate(target with { StartedAt = null }); }
            catch (InvalidOperationException) { rejectedUnknown = true; }
            check(rejectedUnknown && !child.HasExited, "Termination rejects processes whose identity cannot be verified");
            ProcessMemoryReader.Terminate(target);
            check(child.WaitForExit(5000), "Process termination actually stops an isolated test process");
        }
        finally { if (!child.HasExited) { child.Kill(); child.WaitForExit(5000); } }
    }
    private static void Pump(int milliseconds = 400)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(10); }
    }
}
