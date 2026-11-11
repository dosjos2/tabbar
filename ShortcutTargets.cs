using System.Diagnostics;

namespace Tabbar;

internal enum ShortcutKind { Auto, Script, Program, Folder, Website, WindowsShortcut }

internal static class ShortcutTargets
{
    internal static bool IsWebsite(string target) => Uri.TryCreate(target, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && !string.IsNullOrWhiteSpace(uri.Host);

    internal static ShortcutKind Classify(string target)
    {
        if (IsWebsite(target)) return ShortcutKind.Website;
        if (Directory.Exists(target)) return ShortcutKind.Folder;
        if (ScriptRunner.IsScript(target)) return ShortcutKind.Script;
        return System.IO.Path.GetExtension(target).ToLowerInvariant() switch
        {
            ".exe" => ShortcutKind.Program,
            ".lnk" => ShortcutKind.WindowsShortcut,
            _ => ShortcutKind.Auto
        };
    }

    internal static ScriptEntry? Create(string target)
    {
        target = target.Trim();
        var kind = Classify(target);
        if (kind == ShortcutKind.Auto) return null;
        if (kind != ShortcutKind.Website && !File.Exists(target) && !Directory.Exists(target)) return null;
        var path = kind == ShortcutKind.Website ? new Uri(target).AbsoluteUri : System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(target));
        return new ScriptEntry
        {
            Path = path,
            Kind = kind,
            Name = kind == ShortcutKind.Website ? new Uri(path).Host
                : kind == ShortcutKind.Folder ? new DirectoryInfo(path).Name : System.IO.Path.GetFileNameWithoutExtension(path),
            RunAsAdministrator = kind == ShortcutKind.Script,
            UseScriptFolder = kind == ShortcutKind.Program
        };
    }

    internal static bool SameTarget(ScriptEntry a, ScriptEntry b) => string.Equals(a.Path, b.Path,
        a.TargetKind == ShortcutKind.Website || b.TargetKind == ShortcutKind.Website ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    internal static string Label(ScriptEntry entry) => entry.TargetKind switch
    {
        ShortcutKind.Folder => "Mappe",
        ShortcutKind.Website => "Nettside",
        ShortcutKind.Program => "Program",
        ShortcutKind.WindowsShortcut => "Windows-snarvei",
        _ => "Script"
    };

    internal static ProcessStartInfo CreateOpenStartInfo(ScriptEntry entry)
    {
        if (entry.IsScript || entry.TargetKind == ShortcutKind.Auto) throw new InvalidOperationException("Ugyldig snarveitype.");
        if (!entry.Exists) throw new FileNotFoundException("Målet for snarveien finnes ikke.", entry.Path);
        var start = new ProcessStartInfo(entry.Path) { UseShellExecute = true };
        if (entry.TargetKind == ShortcutKind.Program)
        {
            start.Verb = entry.RunAsAdministrator ? "runas" : "";
            start.WorkingDirectory = entry.UseScriptFolder ? System.IO.Path.GetDirectoryName(entry.Path)!
                : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }
        return start;
    }
}
