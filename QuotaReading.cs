using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Tabbar;

internal sealed record QuotaReading(double? Remaining, string Period, DateTimeOffset? ResetsAt = null, bool Unlimited = false, string? Detail = null)
{
    internal string? ResetNote { get; init; }
    internal static QuotaReading Unavailable(string detail) => new(null, "Ikke tilgjengelig", Detail: detail);
}

internal static class QuotaReader
{
    internal static async Task<QuotaReading> ReadAsync(bool copilot, CancellationToken cancellation, ProviderSettings? settings = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var executable = FindExecutable(copilot, settings);
            if (executable is null) return QuotaReading.Unavailable($"{(copilot ? "Copilot" : "Codex")} CLI ble ikke funnet. Installer CLI og logg inn.");
            using var rpc = new QuotaRpc(executable, copilot, settings);
            if (copilot)
            {
                try { await rpc.CallAsync("connect", new { supportedTaskKinds = Array.Empty<string>() }, timeout.Token); }
                catch (QuotaRpcException e) when (e.Code == -32601) { await rpc.CallAsync("ping", new { }, timeout.Token); }
                return ParseCopilot(await rpc.CallAsync("account.getQuota", new { }, timeout.Token));
            }
            await rpc.CallAsync("initialize", new { clientInfo = new { name = "tabbar", title = "Tabbar", version = "1.7.1" } }, timeout.Token);
            await rpc.NotifyAsync("initialized", timeout.Token);
            return ParseCodex(await rpc.CallAsync("account/rateLimits/read", new { }, timeout.Token));
        }
        catch (OperationCanceledException) { return QuotaReading.Unavailable("Kvoteavlesningen tok for lang tid. Prøver igjen automatisk."); }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { return QuotaReading.Unavailable("Kunne ikke hente kvote. Kontroller at CLI er oppdatert og at du er logget inn.\nFeilkode: "
            + (e is System.ComponentModel.Win32Exception win ? $"Windows {win.NativeErrorCode}" : e is QuotaRpcException rpc ? $"RPC {rpc.Code}" : e.GetType().Name)); }
    }

    internal static QuotaReading ParseCodex(JsonElement result)
    {
        var limits = result.Get("rateLimits");
        var windows = new List<QuotaReading>();
        foreach (var key in new[] { "primary", "secondary" })
        {
            var window = limits.Get(key);
            if (window.Number("usedPercent") is not { } used) continue;
            var minutes = window.Number("windowDurationMins");
            var period = minutes >= 10080 ? "Uke" : minutes is > 0 ? $"{minutes / 60:0.#} timer" : "Periode";
            DateTimeOffset? reset = window.Number("resetsAt") is { } seconds ? DateTimeOffset.FromUnixTimeSeconds((long)seconds) : null;
            windows.Add(new(Math.Clamp(100 - used, 0, 100), period, reset));
        }
        if (windows.Count > 0)
        {
            var limiting = windows.MinBy(w => w.Remaining)!;
            return limiting with { Detail = string.Join("\n", windows.Select(w => $"{w.Period}: {w.Remaining:0.#}% igjen" + (w.ResetsAt is { } reset ? $", nullstilles {reset.LocalDateTime:g}" : ""))) };
        }
        var individual = limits.Get("individualLimit");
        if (individual.Number("remainingPercent") is { } left)
            return new(Math.Clamp(left, 0, 100), "Brukergrense", individual.Number("resetsAt") is { } epoch ? DateTimeOffset.FromUnixTimeSeconds((long)epoch) : null);
        // Unlimited extra credits do not imply an unlimited subscription allowance.
        return QuotaReading.Unavailable("Kontoen rapporterer ingen abonnementskvote i prosent. Kredittsaldo er ikke en tokenkvote.");
    }

    internal static QuotaReading ParseCopilot(JsonElement result, DateTimeOffset? observedAt = null)
    {
        var quotas = result.Get("quotaSnapshots");
        var premium = quotas.Get("premium_interactions");
        if (premium.ValueKind != JsonValueKind.Object)
            return QuotaReading.Unavailable("Copilot rapporterer ingen premiumkvote for denne kontoen.");
        var entitlement = premium.Number("entitlementRequests");
        var unlimited = entitlement == -1;
        var remaining = premium.Number("remainingPercentage");
        if (!unlimited && remaining is null) return QuotaReading.Unavailable("Copilot rapporterer ingen gjenstående prosent.");
        // Some CLI versions put the fetch timestamp in resetDate (copilot-sdk#2619).
        // The monthly allowance resets on the first at 00:00 UTC, independently of billing date.
        // https://docs.github.com/en/copilot/reference/copilot-billing/request-based-billing-legacy/monitor-premium-requests
        var utc = (observedAt ?? DateTimeOffset.UtcNow).ToUniversalTime();
        DateTimeOffset? reset = unlimited ? null : new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
        return new(remaining is { } value ? Math.Clamp(value, 0, 100) : null, "Premium", reset, unlimited,
            entitlement is >= 0 && premium.Number("usedRequests") is { } used ? $"{used:0.#} av {entitlement:0.#} premiumforespørsler brukt" : "Copilot-abonnementets premiumkvote")
        { ResetNote = unlimited ? null : "Beregnet fra GitHubs månedlige kvoteperiode: den 1. kl. 00:00 UTC. Nullstilling vises i lokal tid." };
    }

    internal static string? FindExecutable(bool copilot, ProviderSettings? settings = null)
    {
        if (!string.IsNullOrWhiteSpace(settings?.ExecutablePath))
            return File.Exists(settings.ExecutablePath) ? settings.ExecutablePath : null;
        var name = copilot ? "copilot.exe" : "codex.exe";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(directory.Trim('"'), name);
            if (File.Exists(candidate)) return candidate;
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (copilot)
        {
            var link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", name);
            return File.Exists(link) ? link : null;
        }
        var codexHome = !string.IsNullOrWhiteSpace(settings?.ProfileDirectory) ? settings.ProfileDirectory : Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(home, ".codex");
        var releases = Path.Combine(codexHome, "packages", "app-server-daemon", "releases");
        if (!Directory.Exists(releases)) return null;
        return Directory.EnumerateDirectories(releases).OrderByDescending(Directory.GetLastWriteTimeUtc)
            .Select(d => Path.Combine(d, "bin", name)).FirstOrDefault(File.Exists);
    }

    internal static void ApplyProfile(ProcessStartInfo start, bool copilot, ProviderSettings? settings)
    {
        if (!string.IsNullOrWhiteSpace(settings?.ProfileDirectory))
            start.Environment[copilot ? "COPILOT_HOME" : "CODEX_HOME"] = settings.ProfileDirectory;
    }

    internal static ProcessStartInfo LoginStartInfo(bool copilot, ProviderSettings settings)
    {
        var executable = FindExecutable(copilot, settings) ?? throw new InvalidOperationException("CLI ble ikke funnet. Velg programfilen eller installer CLI først.");
        // A console shows login progress and any device code. The provider owns credential entry.
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = false };
        start.ArgumentList.Add("login");
        ApplyProfile(start, copilot, settings);
        return start;
    }

    internal static JsonElement Get(this JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : default;
    internal static double? Number(this JsonElement value, string name) => value.Get(name) is { ValueKind: JsonValueKind.Number } number && number.TryGetDouble(out var result) && double.IsFinite(result) ? result : null;
    internal static string? String(this JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

// Read-only CLI RPC: no conversations, prompts, credentials or model requests.
internal sealed class QuotaRpcException(int code) : InvalidOperationException("Quota RPC failed.")
{
    internal int Code { get; } = code;
}

internal sealed class QuotaRpc : IDisposable
{
    private readonly Process process;
    private readonly bool framed;
    private int nextId;

    internal QuotaRpc(string executable, bool copilot, ProviderSettings? settings = null)
    {
        framed = copilot;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8 };
        QuotaReader.ApplyProfile(start, copilot, settings);
        foreach (var arg in copilot ? new[] { "--headless", "--stdio", "--no-auto-update", "--log-level", "none" } : new[] { "app-server" }) start.ArgumentList.Add(arg);
        process = Process.Start(start) ?? throw new IOException("CLI could not start.");
        // Drain diagnostics without retaining authentication or account data.
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
    }

    internal Task NotifyAsync(string method, CancellationToken token) => SendAsync(new { jsonrpc = "2.0", method }, token);
    private async Task SendAsync(object message, CancellationToken token)
    {
        var json = JsonSerializer.Serialize(message);
        var wire = framed ? $"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n{json}" : json + "\n";
        await process.StandardInput.WriteAsync(wire.AsMemory(), token);
        await process.StandardInput.FlushAsync(token);
    }

    internal async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken token)
    {
        var id = ++nextId;
        await SendAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, token);
        while (true)
        {
            string json;
            if (framed)
            {
                var length = 0;
                while (true)
                {
                    var header = new StringBuilder();
                    var one = new byte[1];
                    while (true)
                    {
                        if (await process.StandardOutput.BaseStream.ReadAsync(one, token) == 0) throw new IOException("CLI closed.");
                        if (one[0] == 10) break;
                        header.Append((char)one[0]);
                        if (header.Length > 8192) throw new IOException("Invalid RPC header.");
                    }
                    var line = header.ToString().TrimEnd('\r');
                    if (line.Length == 0) break;
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(line[15..].Trim(), out length);
                }
                if (length <= 0 || length > 4 * 1024 * 1024) throw new IOException("Invalid RPC size.");
                var bytes = new byte[length];
                await process.StandardOutput.BaseStream.ReadExactlyAsync(bytes, token);
                json = Encoding.UTF8.GetString(bytes);
            }
            else json = await process.StandardOutput.ReadLineAsync(token) ?? throw new IOException("CLI closed.");
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.Get("id").ValueKind != JsonValueKind.Number || root.Get("id").GetInt32() != id) continue;
            if (root.Get("error").ValueKind == JsonValueKind.Object) throw new QuotaRpcException((int)(root.Get("error").Number("code") ?? 0));
            return root.Get("result").Clone();
        }
    }

    public void Dispose()
    {
        try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
        process.Dispose();
    }
}
