using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Orisumi.Platform;

namespace Orisumi.Cli.Capture;

internal static partial class CaptureStatus
{
    private const string Ok = "ok";
    private const string Attention = "attention";
    private const string Stopped = "stopped";
    private const int RecentRunLimit = 5;
    private const int RecentFailureLimit = 10;

    internal static async Task Write(Instance instance, SystemLocations locations, bool json, TextWriter output,
        CancellationToken token, string? baseDirectory = null)
    {
        var store = instance.Store;
        var programs = await CollectorPrograms.Read(store.Root, token, baseDirectory);
        var versions = programs.ToDictionary(program => program.Description.Name, program => program.Description.Version,
            StringComparer.Ordinal);
        var prefix = instance.CaptureAgentPrefix;
        var names = versions.Keys.Union(Launchd.CaptureAgentPlists(instance, locations)
            .Select(path => Path.GetFileNameWithoutExtension(path)[prefix.Length..]), StringComparer.Ordinal)
            .Union(store.Sources(), StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var collectors = new List<CollectorStatus>();
        foreach (var name in names)
        {
            var agent = await ReadAgent(locations, instance.CaptureAgentLabel(name), token);
            var (runs, failures, recentFailures) = ReadHistory(store.Databases(name), now - 3_600_000);
            var run = runs.FirstOrDefault() is { } latest ? latest with { Database = null } : null;
            var version = versions.GetValueOrDefault(name);
            var (health, problems) = Assess(version, agent, run, now);
            collectors.Add(new(name, version, agent, run, failures, health, problems, runs, recentFailures));
        }

        var overallHealth = collectors.Count > 0 && collectors.All(collector => collector.Health == Stopped) ? Stopped
            : collectors.Any(collector => collector.Health != Ok) ? Attention : Ok;
        var status = new Status(instance.Name, store.Root, Commands.HostVersion, collectors, now, overallHealth);
        if (json)
            output.WriteLine(JsonSerializer.Serialize(status, StatusJson.Default.Status));
        else
            WriteText(status, output);
    }

    // Read failures are evidence, not health: short AX timeouts happen every hour (see capture/index.mdx).
    private static (string Health, List<string> Problems) Assess(string? version, AgentStatus agent, RunStatus? run, long now)
    {
        var problems = new List<string>();
        var stopped = version is not null && agent.State == "not_loaded";
        if (version is null) problems.Add("not_installed");
        if (stopped) problems.Add("not_loaded");
        if (agent.State is not ("not_loaded" or "running")) problems.Add("not_running");
        if (run?.StopReason == "failed") problems.Add("run_failed");
        if (run is { StoppedAtUtcMs: null } &&
            Math.Max(run.ConfirmedUntilUtcMs ?? run.StartedAtUtcMs, run.StartedAtUtcMs) < now - 300_000)
            problems.Add("stale");

        return (stopped ? Stopped : problems.Count > 0 ? Attention : Ok, problems);
    }

    private static async Task<AgentStatus> ReadAgent(SystemLocations locations, string label, CancellationToken token)
    {
        var service = Launchd.Service(label);
        var output = await Launchd.Print(locations, service, token);
        if (output is null) return new("not_loaded", null, null);
        var state = Launchd.Field(output, "state") ?? throw new InvalidDataException($"launchctl print {service}: missing state");
        if (state == "running") return new(state, Launchd.Pid(output), null);
        var exit = Launchd.Field(output, "last exit code");
        var lastExitCode = exit switch
        {
            null or "(never exited)" => null,
            _ when long.TryParse(exit, CultureInfo.InvariantCulture, out var code) => JsonValue.Create(code),
            _ => JsonValue.Create(exit),
        };
        return new(state, null, lastExitCode);
    }

    private static (RunStatus[] Runs, long Failures, ReadFailure[] RecentFailures) ReadHistory(IReadOnlyList<string> paths, long after)
    {
        var runs = new List<RunStatus>();
        var recentFailures = new List<ReadFailure>();
        long failures = 0;
        foreach (var path in paths)
        {
            using var database = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 3,
            }.ToString());
            database.Open();
            var name = Path.GetFileNameWithoutExtension(path);
            using var command = database.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='capture_runs'";
            if (command.ExecuteScalar() is 0L) continue;
            command.CommandText = """
                SELECT run_id, started_at_utc_ms, collector_version, confirmed_until_utc_ms,
                       stopped_at_utc_ms, stop_reason, error FROM capture_runs
                ORDER BY started_at_utc_ms DESC, run_id DESC LIMIT $run_limit
                """;
            command.Parameters.AddWithValue("$run_limit", RecentRunLimit);
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    runs.Add(new(reader.GetInt64(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetInt64(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), name));

            command.CommandText = """
                SELECT observed_at_utc_ms, run_id, scope, error,
                       (SELECT COUNT(*) FROM capture_read_failures WHERE observed_at_utc_ms >= $after)
                FROM capture_read_failures
                ORDER BY observed_at_utc_ms DESC, event_id DESC LIMIT $failure_limit
                """;
            command.Parameters.AddWithValue("$after", after);
            command.Parameters.AddWithValue("$failure_limit", RecentFailureLimit);
            using var failureReader = command.ExecuteReader();
            if (!failureReader.Read()) continue;
            failures += failureReader.GetInt64(4);
            do
            {
                recentFailures.Add(new(failureReader.GetInt64(0), failureReader.GetInt64(1),
                    failureReader.IsDBNull(2) ? null : failureReader.GetString(2), failureReader.GetString(3), name));
            } while (failureReader.Read());
        }
        // Stable sorting keeps ties in database path order, then in descending local ID order within a database.
        return (runs.OrderByDescending(run => run.StartedAtUtcMs).Take(RecentRunLimit).ToArray(), failures,
            recentFailures.OrderByDescending(failure => failure.ObservedAtUtcMs).Take(RecentFailureLimit).ToArray());
    }

    private static void WriteText(Status status, TextWriter output)
    {
        output.WriteLine($"{status.Instance} ({status.Store}) host {status.HostVersion}, health {status.Health}");
        foreach (var collector in status.Collectors)
        {
            var agent = collector.Agent;
            var process = agent.Pid is int pid ? $" (pid {pid})"
                : agent.LastExitCode is { } exit ? $" (last exit {exit})" : "";
            var run = collector.Run;
            var capture = run is null ? "no run" : FormattableString.Invariant(
                $"started {DateTimeOffset.FromUnixTimeMilliseconds(run.StartedAtUtcMs):yyyy-MM-ddTHH:mm:ss'Z'}, confirmed {Confirmed(run.ConfirmedUntilUtcMs, status.GeneratedAtUtcMs)}");
            if (run?.StoppedAtUtcMs is not null) capture += $", stopped ({run.StopReason})";
            var problems = collector.Problems.Count > 0 ? $" [{string.Join(", ", collector.Problems)}]" : "";
            output.WriteLine($"  {collector.Name} {collector.Version ?? "-"}: {agent.State}{process}, {capture}, failures (1h) {collector.ReadFailuresLastHour}{problems}");
        }
    }

    private static string Confirmed(long? confirmed, long now)
    {
        if (confirmed is null) return "-";
        // `now` is taken before the databases are read, so a running collector can confirm after it.
        var seconds = Math.Max(0, now - confirmed.Value) / 1000;
        return seconds switch
        {
            >= 86400 => $"{seconds / 86400}d ago",
            >= 3600 => $"{seconds / 3600}h ago",
            >= 60 => $"{seconds / 60}m ago",
            _ => $"{seconds}s ago",
        };
    }

    private sealed record Status(string Instance, string Store, string HostVersion, List<CollectorStatus> Collectors,
        long GeneratedAtUtcMs, string Health);
    private sealed record CollectorStatus(string Name, string? Version, AgentStatus Agent, RunStatus? Run, long ReadFailuresLastHour,
        string Health, List<string> Problems, RunStatus[] RecentRuns, ReadFailure[] RecentReadFailures);
    private sealed record AgentStatus(string State, int? Pid, JsonValue? LastExitCode);
    private sealed record RunStatus(long RunId, long StartedAtUtcMs, string? CollectorVersion, long? ConfirmedUntilUtcMs,
        long? StoppedAtUtcMs, string? StopReason, string? Error,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Database);
    private sealed record ReadFailure(long ObservedAtUtcMs, long RunId, string? Scope, string Error, string Database);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(Status))]
    private sealed partial class StatusJson : JsonSerializerContext;
}
