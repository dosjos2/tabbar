using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace Tabbar;

internal sealed class ScriptEntry
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public ShortcutKind Kind { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public ShortcutKind TargetKind => Kind == ShortcutKind.Auto ? ShortcutTargets.Classify(Path) : Kind;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsScript => TargetKind == ShortcutKind.Script;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool CanElevate => TargetKind is ShortcutKind.Script or ShortcutKind.Program;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Exists => TargetKind == ShortcutKind.Website ? ShortcutTargets.IsWebsite(Path) : TargetKind == ShortcutKind.Folder ? Directory.Exists(Path) : File.Exists(Path);
    public bool UseScriptFolder { get; set; }
    public bool RunAsAdministrator { get; set; } = true;
    public string? IconPath { get; set; }
    public string? LastLogPath { get; set; }
    public int? LastExitCode { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public string? LastRunError { get; set; }
}

internal sealed class Settings
{
    public int Width { get; set; } = 150;
    public string? Monitor { get; set; }
    public bool KeepConsole { get; set; } = true;
    public int IconHeight { get; set; } = 32;
    public GaugeSettings Gauges { get; set; } = new();
    public List<ScriptEntry> Scripts { get; set; } = [];
}

internal sealed class SettingsStore(string directory)
{
    internal string DirectoryPath => directory;
    internal string FilePath => System.IO.Path.Combine(directory, "settings.json");
    internal Settings Load()
    {
        if (!File.Exists(FilePath)) return new();
        try
        {
            var result = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? throw new InvalidDataException("Tom innstillingsfil.");
            result.Width = Math.Clamp(result.Width, 100, 600);
            result.IconHeight = Math.Clamp(result.IconHeight, 16, 128);
            result.Scripts ??= [];
            result.Gauges ??= new();
            result.Gauges.Normalize();
            result.Scripts.RemoveAll(s => s is null || string.IsNullOrWhiteSpace(s.Path));
            RefreshResults(result);
            return result;
        }
        catch (Exception e) when (e is JsonException or InvalidDataException)
        {
            File.Copy(FilePath, FilePath + ".invalid-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff"));
            MessageBox.Show("Innstillingene kunne ikke leses. En kopi er bevart i:\n" + directory, "Tabbar");
            return new();
        }
    }
    internal void Save(Settings settings)
    {
        Directory.CreateDirectory(directory);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, true);
    }

    internal static bool RefreshResults(Settings settings)
    {
        var changed = false;
        foreach (var entry in settings.Scripts.Where(s => s.LastExitCode is null && s.LastLogPath is not null))
        {
            try
            {
                var resultPath = System.IO.Path.ChangeExtension(entry.LastLogPath!, ".json");
                if (!File.Exists(resultPath)) continue;
                var result = JsonSerializer.Deserialize<RunResult>(File.ReadAllText(resultPath));
                if (result is null) continue;
                entry.LastExitCode = result.ExitCode;
                entry.LastRunError = result.Error;
                changed = true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return changed;
    }
}

internal sealed class GaugeSettings
{
    public bool AutoRotate { get; set; } = true;
    public int SlideSeconds { get; set; } = 8;
    public int RefreshMinutes { get; set; } = 5;
    public ProviderSettings Codex { get; set; } = new();
    public ProviderSettings Copilot { get; set; } = new();
    internal void Normalize()
    {
        SlideSeconds = Math.Clamp(SlideSeconds, 3, 120);
        RefreshMinutes = Math.Clamp(RefreshMinutes, 1, 60);
        Codex ??= new(); Copilot ??= new();
    }
    internal GaugeSettings Copy() => new() { AutoRotate = AutoRotate, SlideSeconds = SlideSeconds, RefreshMinutes = RefreshMinutes,
        Codex = Codex with { }, Copilot = Copilot with { } };
}

internal sealed record ProviderSettings
{
    public string ExecutablePath { get; set; } = "";
    public string ProfileDirectory { get; set; } = "";
}

internal static class ScriptRunner
{
    internal sealed record LaunchRequest(string ScriptPath, string WorkingDirectory, bool KeepConsole, string? LogPath = null, string? ResultPath = null, bool ShowConsole = true);

    internal static ProcessStartInfo CreateLaunchStartInfo(ScriptEntry entry, bool keepConsole, string? logPath = null, string? resultPath = null, bool showConsole = true)
    {
        var command = CreateStartInfo(entry, keepConsole);
        // Pass the resolved desktop path so using another administrator account
        // in UAC does not switch to that account's desktop.
        var request = new LaunchRequest(command.Environment["TABBAR_SCRIPT"]!, command.WorkingDirectory, keepConsole, logPath, resultPath, showConsole);
        return new ProcessStartInfo
        {
            FileName = Environment.ProcessPath!,
            Arguments = "--run-script " + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(request)),
            UseShellExecute = entry.RunAsAdministrator,
            Verb = entry.RunAsAdministrator ? "runas" : ""
        };
    }

    internal static ProcessStartInfo ReadLaunchRequest(string encodedRequest)
    {
        var request = DecodeRequest(encodedRequest);
        return CreateStartInfo(new ScriptEntry { Path = request.ScriptPath }, request.LogPath is null && request.KeepConsole, request.WorkingDirectory);
    }

    internal static LaunchRequest DecodeRequest(string encodedRequest) => JsonSerializer.Deserialize<LaunchRequest>(Convert.FromBase64String(encodedRequest))
        ?? throw new InvalidDataException("Ugyldig scriptstart.");

    internal static ProcessStartInfo CreateStartInfo(ScriptEntry entry, bool keepConsole, string? resolvedWorkingDirectory = null)
    {
        if (!File.Exists(entry.Path)) throw new FileNotFoundException("Scriptet er flyttet eller slettet.", entry.Path);
        if (!IsScript(entry.Path)) throw new InvalidOperationException("Velg en .bat-, .cmd- eller .ps1-fil.");
        var workingDirectory = resolvedWorkingDirectory ?? (entry.UseScriptFolder ? System.IO.Path.GetDirectoryName(entry.Path)! : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        if (!Directory.Exists(workingDirectory)) throw new DirectoryNotFoundException("Arbeidsmappen finnes ikke: " + workingDirectory);
        if (System.IO.Path.GetExtension(entry.Path).Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            var powershell = new ProcessStartInfo
            {
                FileName = System.IO.Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                WorkingDirectory = workingDirectory,
                UseShellExecute = false
            };
            powershell.ArgumentList.Add("-NoLogo");
            powershell.ArgumentList.Add("-NoProfile");
            if (keepConsole) powershell.ArgumentList.Add("-NoExit");
            powershell.ArgumentList.Add("-File");
            powershell.ArgumentList.Add(System.IO.Path.GetFullPath(entry.Path));
            powershell.Environment["TABBAR_SCRIPT"] = System.IO.Path.GetFullPath(entry.Path);
            return powershell;
        }
        var start = new ProcessStartInfo
        {
            FileName = System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            // Environment expansion handles spaces, &, parentheses and literal % in paths.
            Arguments = "/d /v:off /s " + (keepConsole ? "/k" : "/c") + " \"\"%TABBAR_SCRIPT%\"\"",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false
        };
        start.Environment["TABBAR_SCRIPT"] = System.IO.Path.GetFullPath(entry.Path);
        return start;
    }
    internal static bool IsScript(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() is ".bat" or ".cmd" or ".ps1";
}

internal static class Startup
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal static bool Enabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(Key); return key?.GetValue("Tabbar") is string; }
    }
    internal static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        if (enabled) key.SetValue("Tabbar", "\"" + Environment.ProcessPath + "\"");
        else key.DeleteValue("Tabbar", false);
    }
}
