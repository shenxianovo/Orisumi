using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orisumi.App;

// What `orisumi capture status --json` reports; the CLI decides health, the tray only words it.
// Words and rules follow the macOS app (Host/Platforms/MacOS/App) so both platforms say the same thing.

internal enum Health { Ok, Attention, Stopped, Unavailable }

internal sealed class CaptureStatus
{
    public string Instance { get; init; } = "";
    public string? HostVersion { get; init; }
    public long GeneratedAtUtcMs { get; init; }
    public string Health { get; init; } = "unavailable";
    public List<Collector> Collectors { get; init; } = [];
}

internal sealed class Collector
{
    public string Name { get; init; } = "";
    public string Health { get; init; } = "unavailable";
    public List<string> Problems { get; init; } = [];
    public Agent? Agent { get; init; }
    public Run? Run { get; init; }
}

internal sealed class Agent
{
    public string State { get; init; } = "";
}

internal sealed class Run
{
    public long StartedAtUtcMs { get; init; }
    public long? ConfirmedUntilUtcMs { get; init; }
    public long? StoppedAtUtcMs { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(CaptureStatus))]
internal sealed partial class StatusJson : JsonSerializerContext;

internal static class Presentation
{
    internal static CaptureStatus Parse(string json) =>
        JsonSerializer.Deserialize(json, StatusJson.Default.CaptureStatus) ?? throw new JsonException("Empty status.");

    internal static Health HealthOf(string value) => value switch
    {
        "ok" => Health.Ok,
        "attention" => Health.Attention,
        "stopped" => Health.Stopped,
        _ => Health.Unavailable,
    };

    internal static string Title(string? instance) => instance switch
    {
        "prod" => "Orisumi",
        "dev" => "Orisumi Dev",
        null or "" => "Orisumi",
        _ => $"Orisumi ({instance})",
    };

    internal static string DisplayName(string name) => name switch
    {
        "macos" => "macOS",
        "windows" => "Windows",
        "browser" => "Browser",
        "claude" => "Claude Code",
        "codex" => "Codex",
        _ => string.Join(' ', name.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..])),
    };

    /// One sentence that says what is going on: the first menu line and the tooltip.
    internal static string Headline(CaptureStatus status)
    {
        var collectors = status.Collectors;
        var total = collectors.Count;
        if (total == 0) return "No sources installed";
        var unhealthy = collectors.Where(collector => HealthOf(collector.Health) != Health.Ok).ToList();
        var stopped = unhealthy.Where(collector => HealthOf(collector.Health) == Health.Stopped).ToList();
        if (unhealthy.Count == 0) return total == 1 ? $"{DisplayName(collectors[0].Name)} capturing" : $"All {total} sources capturing";
        if (stopped.Count == total) return "Capture stopped";
        if (stopped.Count == unhealthy.Count)
            return stopped.Count == 1 ? $"{DisplayName(stopped[0].Name)} stopped" : $"{stopped.Count} of {total} sources stopped";
        return unhealthy.Count == 1 ? $"{DisplayName(unhealthy[0].Name)} needs attention" : $"{unhealthy.Count} sources need attention";
    }

    /// What a source is doing in a few words ("Capturing", "Not running").
    internal static string Summary(Collector collector, long nowUtcMs)
    {
        if (collector.Problems.FirstOrDefault() is { } problem)
            return problem switch
            {
                "not_loaded" => "Stopped",
                "not_installed" => "Not installed",
                "not_running" => "Not running",
                "run_failed" => "Last run failed",
                "stale" => collector.Run is { } run
                    ? $"No confirmation for {Elapsed(run.ConfirmedUntilUtcMs ?? run.StartedAtUtcMs, nowUtcMs)}"
                    : "No recent confirmation",
                _ => Humanized(problem),
            };
        if (collector.Run is not { StoppedAtUtcMs: null })
            // The browser has no open run while Chrome is closed; that is not a problem.
            return collector.Name == "browser" ? "Waiting for Chrome" : "Idle";
        return "Capturing";
    }

    /// Whether capture is set up to run, judged from problems, not the process state: with the Windows supervisor
    /// down the state reads `not_loaded` while the source is still wanted (`not_running`), and Stop must stay available.
    internal static bool AnyLoaded(CaptureStatus status) =>
        status.Collectors.Any(collector => collector.Agent is not null &&
            !collector.Problems.Contains("not_loaded") && !collector.Problems.Contains("not_installed"));

    /// "42s", "5m", "1h 5m", "2d 3h": the two largest units, like the macOS app.
    internal static string Elapsed(long fromUtcMs, long toUtcMs)
    {
        var span = TimeSpan.FromMilliseconds(Math.Max(0, toUtcMs - fromUtcMs));
        if (span.TotalSeconds < 60) return $"{(int)span.TotalSeconds}s";
        var parts = new List<string>();
        if (span.Days > 0) parts.Add($"{span.Days}d");
        if (span.Hours > 0) parts.Add($"{span.Hours}h");
        if (span.Minutes > 0) parts.Add($"{span.Minutes}m");
        return string.Join(' ', parts.Take(2));
    }

    internal static string Humanized(string code)
    {
        var words = code.Replace('_', ' ');
        return words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];
    }
}
