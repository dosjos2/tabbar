using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tabbar;

internal sealed record RunResult(int ExitCode, DateTimeOffset FinishedAt, string? Error = null);
internal sealed record ExecutionOutcome(string LogPath, RunResult Result);

internal sealed class ScriptExecution(string logDirectory)
{
    private readonly HashSet<string> active = new(StringComparer.OrdinalIgnoreCase);
    private string ScriptFolder(string path) => Path.Combine(logDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())))[..20]);
    internal bool IsRunning(string path)
    {
        lock (active) if (active.Contains(Path.GetFullPath(path))) return true;
        var lockPath = Path.Combine(ScriptFolder(path), "running.lock");
        if (!File.Exists(lockPath)) return false;
        try { using var file = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    internal async Task<ExecutionOutcome> RunAsync(ScriptEntry entry, bool keepConsole, Action<string>? started = null, bool showConsole = true)
    {
        var path = Path.GetFullPath(entry.Path);
        if (IsRunning(path)) throw new InvalidOperationException("Scriptet kjører allerede.");
        lock (active) if (!active.Add(path)) throw new InvalidOperationException("Scriptet kjører allerede.");
        try
        {
            var folder = ScriptFolder(path);
            Directory.CreateDirectory(folder);
            var runName = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
            var log = Path.Combine(folder, runName + ".log");
            var resultPath = Path.Combine(folder, runName + ".json");
            var start = ScriptRunner.CreateLaunchStartInfo(entry, keepConsole, log, resultPath, showConsole);
            File.WriteAllText(log, $"Tabbar – {entry.Name}\r\nScript: {path}\r\nStart: {DateTimeOffset.Now:O}\r\nAdministrator: {(entry.RunAsAdministrator ? "Ja" : "Nei")}\r\n\r\n", new UTF8Encoding(false));
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Kunne ikke starte scriptprosessen.");
            started?.Invoke(log);
            // The helper publishes the result before waiting for Enter, so status
            // reflects the script's exit rather than the console window's lifetime.
            while (!File.Exists(resultPath) && !process.HasExited) await Task.Delay(150);
            var result = File.Exists(resultPath)
                ? JsonSerializer.Deserialize<RunResult>(await File.ReadAllTextAsync(resultPath)) ?? throw new InvalidDataException("Ugyldig kjøreresultat.")
                : new RunResult(-1, DateTimeOffset.Now, "Scriptvinduet ble lukket, eller oppstartshjelperen stoppet før resultatet kunne leses.");
            return new ExecutionOutcome(log, result);
        }
        finally { lock (active) active.Remove(path); }
    }
}

internal static class ExecutionHelper
{
    internal static int Run(ScriptRunner.LaunchRequest request)
    {
        if (request.LogPath is null || request.ResultPath is null)
        {
            using var legacy = Process.Start(ScriptRunner.CreateStartInfo(new ScriptEntry { Path = request.ScriptPath }, request.KeepConsole, request.WorkingDirectory))!;
            legacy.WaitForExit();
            return legacy.ExitCode;
        }

        var hasConsole = request.ShowConsole && Native.AllocConsole();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding((int)Native.GetOEMCP());
        if (hasConsole)
        {
            Console.InputEncoding = encoding;
            Console.OutputEncoding = encoding;
            Console.SetIn(new StreamReader(Console.OpenStandardInput(), encoding));
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), encoding) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), encoding) { AutoFlush = true });
            Console.Title = "Tabbar – " + Path.GetFileName(request.ScriptPath);
        }

        RunResult result;
        try
        {
            // An exclusive writer also prevents duplicate runs after the toolbar
            // restarts while this helper is still executing the script.
            using var runLock = new FileStream(Path.Combine(Path.GetDirectoryName(request.LogPath)!, "running.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            using var file = new FileStream(request.LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var log = new StreamWriter(file, new UTF8Encoding(false)) { AutoFlush = true };
            log.WriteLine("Arbeidsmappe: " + request.WorkingDirectory);
            log.WriteLine();
            var start = ScriptRunner.CreateStartInfo(new ScriptEntry { Path = request.ScriptPath }, false, request.WorkingDirectory);
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.StandardOutputEncoding = encoding;
            start.StandardErrorEncoding = encoding;
            start.CreateNoWindow = !hasConsole;
            start.RedirectStandardInput = !hasConsole;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Kunne ikke starte scriptprosessen.");
            if (!hasConsole) process.StandardInput.Close();
            var gate = new object();
            var capturing = true;
            async Task Copy(StreamReader reader, TextWriter console)
            {
                try
                {
                    var buffer = new char[2048];
                    int count;
                    while ((count = await reader.ReadAsync(buffer)) > 0)
                    {
                        lock (gate)
                        {
                            if (!capturing) return;
                            log.Write(buffer, 0, count);
                            if (hasConsole) console.Write(buffer, 0, count);
                        }
                    }
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException) { }
                finally { reader.Dispose(); }
            }
            var output = Copy(process.StandardOutput, Console.Out);
            var error = Copy(process.StandardError, Console.Error);
            process.WaitForExit();
            // Do not wait forever for detached descendants that inherit pipe handles.
            var drained = Task.WhenAll(output, error).Wait(TimeSpan.FromSeconds(3));
            result = new RunResult(process.ExitCode, DateTimeOffset.Now);
            lock (gate)
            {
                capturing = false;
                if (!drained) log.WriteLine("\r\n[Bakgrunnsprosesser holder utskriftsstrømmer åpne. Videre utskrift fra disse tas ikke med.]");
                log.WriteLine($"\r\n\r\nSlutt: {result.FinishedAt:O}\r\nAvslutningskode: {result.ExitCode}");
            }
        }
        catch (Exception e)
        {
            result = new RunResult(-1, DateTimeOffset.Now, e.Message);
            try { File.AppendAllText(request.LogPath, "\r\nFEIL: " + e.Message + "\r\n", Encoding.UTF8); } catch (IOException) { }
            if (hasConsole) Console.Error.WriteLine(e.Message);
        }
        File.WriteAllText(request.ResultPath + ".tmp", JsonSerializer.Serialize(result));
        File.Move(request.ResultPath + ".tmp", request.ResultPath, true);
        if (hasConsole && request.KeepConsole)
        {
            Console.WriteLine($"\r\nScriptet er ferdig. Avslutningskode: {result.ExitCode}\r\nTrykk Enter for å lukke vinduet.");
            Console.ReadLine();
        }
        return result.ExitCode;
    }
}
