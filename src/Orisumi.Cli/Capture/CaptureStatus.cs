using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Orisumi.Platform;

namespace Orisumi.Cli.Capture;

internal static partial class CaptureStatus
{
    internal static async Task Write(Instance instance, SystemLocations locations, bool json, TextWriter output,
        CancellationToken token, string? baseDirectory = null)
    {
        var store = instance.Store;
        var programs = await CollectorPrograms.Read(store.Root, token, baseDirectory);
        var versions = programs.ToDictionary(program => program.Description.Name, program => program.Description.Version,
            StringComparer.Ordinal);
        var prefix = instance.AgentLabel("");
        var names = versions.Keys.Union(Launchd.AgentPlists(instance, locations)
            .Select(path => Path.GetFileNameWithoutExtension(path)[prefix.Length..]), StringComparer.Ordinal)
            .Union(store.Sources(), StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        var after = now.AddHours(-1).ToUnixTimeMilliseconds();
        var collectors = new List<CollectorStatus>();
        foreach (var name in names)
        {
            var agent = await ReadAgent(locations, instance.AgentLabel(name), token);
            var (run, failures) = ReadRuns(store.Databases(name), after);
            collectors.Add(new(name, versions.GetValueOrDefault(name), agent, run, failures));
        }

        var status = new Status(instance.Name, store.Root, Commands.HostVersion, collectors);
        if (json)
            output.WriteLine(JsonSerializer.Serialize(status, StatusJson.Default.Status));
        else
            WriteText(status, now.ToUnixTimeMilliseconds(), output);
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

    private static (RunStatus? Run, long Failures) ReadRuns(IReadOnlyList<string> paths, long after)
    {
        RunStatus? run = null;
        long failures = 0;
        foreach (var path in paths)
        {
            using var database = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 3,
            }.ToString());
            database.Open();
            using var command = database.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='capture_runs'";
            if (command.ExecuteScalar() is 0L) continue;
            command.CommandText = """
                SELECT run_id, started_at_utc_ms, collector_version, confirmed_until_utc_ms,
                       stopped_at_utc_ms, stop_reason, error FROM capture_runs
                ORDER BY started_at_utc_ms DESC, run_id DESC LIMIT 1
                """;
            using (var reader = command.ExecuteReader())
                if (reader.Read() && (run is null || reader.GetInt64(1) > run.StartedAtUtcMs))
                    run = new(reader.GetInt64(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetInt64(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6));
            command.CommandText = "SELECT COUNT(*) FROM capture_read_failures WHERE observed_at_utc_ms >= $after";
            command.Parameters.AddWithValue("$after", after);
            failures += (long)command.ExecuteScalar()!;
        }
        return (run, failures);
    }

    private static void WriteText(Status status, long now, TextWriter output)
    {
        output.WriteLine($"{status.Instance} ({status.Store}) host {status.HostVersion}");
        foreach (var collector in status.Collectors)
        {
            var agent = collector.Agent;
            var process = agent.Pid is int pid ? $" (pid {pid})"
                : agent.LastExitCode is { } exit ? $" (last exit {exit})" : "";
            var run = collector.Run;
            var capture = run is null ? "no run" : FormattableString.Invariant(
                $"started {DateTimeOffset.FromUnixTimeMilliseconds(run.StartedAtUtcMs):yyyy-MM-ddTHH:mm:ss'Z'}, confirmed {Confirmed(run.ConfirmedUntilUtcMs, now)}");
            if (run?.StoppedAtUtcMs is not null) capture += $", stopped ({run.StopReason})";
            output.WriteLine($"  {collector.Name} {collector.Version ?? "-"}: {agent.State}{process}, {capture}, failures (1h) {collector.ReadFailuresLastHour}");
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

    private sealed record Status(string Instance, string Store, string HostVersion, List<CollectorStatus> Collectors);
    private sealed record CollectorStatus(string Name, string? Version, AgentStatus Agent, RunStatus? Run, long ReadFailuresLastHour);
    private sealed record AgentStatus(string State, int? Pid, JsonValue? LastExitCode);
    private sealed record RunStatus(long RunId, long StartedAtUtcMs, string? CollectorVersion, long? ConfirmedUntilUtcMs,
        long? StoppedAtUtcMs, string? StopReason, string? Error);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(Status))]
    private sealed partial class StatusJson : JsonSerializerContext;
}
