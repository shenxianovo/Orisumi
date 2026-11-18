using Orisumi.Collectors.Storage;
using Microsoft.Data.Sqlite;

namespace Orisumi.Collectors.Network;

internal sealed class NetworkDatabase : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly string runId = Guid.NewGuid().ToString("D");
    private readonly int intervalSeconds;
    private long sequence;
    private long? previousTime;
    private long? previousElapsed;
    private Dictionary<int, ActiveProcess> activeProcesses = [];
    private Dictionary<(long ProcessId, SocketKey Key), List<ActiveSocket>> activeSockets = [];
    private sealed record ActiveProcess(long Id, ProcessSample Sample);
    private sealed record ActiveSocket(long Id, SocketSample Sample);

    internal static string PathFor(string store) => Path.GetFullPath(Path.Combine(store, "raw", "network.sqlite"));

    internal NetworkDatabase(string store, string bootId, int intervalSeconds, long startedAt)
    {
        this.intervalSeconds = intervalSeconds;
        var path = PathFor(store);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false, ForeignKeys = true,
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            SqliteSchema.EnsureCreated(connection);
            command.CommandText = "PRAGMA journal_mode = WAL";
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO capture_runs VALUES($id,'nettop',$boot,$interval,$started,NULL,NULL)";
            command.Parameters.AddWithValue("$id", runId);
            command.Parameters.AddWithValue("$boot", bootId);
            command.Parameters.AddWithValue("$interval", intervalSeconds);
            command.Parameters.AddWithValue("$started", startedAt);
            command.ExecuteNonQuery();
        }
        catch { connection.Dispose(); throw; }
    }

    internal void Sample(long startedAt, long observedAt, long elapsed,
        IReadOnlyList<ProcessSample> processes, string? error = null)
    {
        var continuous = error is null && previousTime is long time && observedAt >= time &&
            observedAt - time <= intervalSeconds * 2000L && previousElapsed is long previous &&
            elapsed >= previous && elapsed - previous <= intervalSeconds * 2000L;
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        object? Execute(string sql, params object?[] values)
        {
            command.CommandText = sql;
            command.Parameters.Clear();
            for (var i = 0; i < values.Length; i++)
                command.Parameters.AddWithValue($"$p{i}", values[i] ?? DBNull.Value);
            return command.ExecuteScalar();
        }
        var sampleId = (long)Execute("""
            INSERT INTO samples(run_id,run_sequence,started_at_utc_ms,observed_at_utc_ms,elapsed_ms,continuous,succeeded,error)
            VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7) RETURNING sample_id
            """, runId, sequence + 1, startedAt, observedAt, elapsed, continuous, error is null, error)!;

        // Values and uncertainty are both state. A change to or from null is retained.
        void Counters(string entity, long id, long? received, long? sent, bool initial)
        {
            var idColumn = entity == "process" ? "process_instance_id" : "socket_id";
            foreach (var (direction, value) in new[] { ("received", received), ("sent", sent) })
                Execute($"""
                    INSERT INTO {entity}_traffic_observations
                    SELECT $p0,$p1,$p2,$p3 WHERE $p4 OR $p3 IS NOT (
                        SELECT bytes FROM {entity}_traffic_observations
                        WHERE {idColumn}=$p1 AND direction=$p2 ORDER BY sample_id DESC LIMIT 1)
                    """, sampleId, id, direction, value, initial);
        }
        var nextProcesses = new Dictionary<int, ActiveProcess>();
        var nextSockets = new Dictionary<(long, SocketKey), List<ActiveSocket>>();
        foreach (var process in error is null ? processes : [])
        {
            activeProcesses.TryGetValue(process.Pid, out var oldProcess);
            var identity = process.Identity;
            object? processId = null;
            if (identity.StartedAtUtcUs is not null)
                processId = Execute("""
                    SELECT process_instance_id FROM process_instances
                    WHERE run_id=$p0 AND pid=$p1 AND started_at_utc_us=$p2
                    """, runId, process.Pid, identity.StartedAtUtcUs);
            else if (continuous && oldProcess is not null && oldProcess.Sample.Identity.StartedAtUtcUs is null &&
                oldProcess.Sample.Name == process.Name && oldProcess.Sample.Identity.ExecutablePath == identity.ExecutablePath &&
                !Decreased(process.ReceivedBytes, process.SentBytes, oldProcess.Sample.ReceivedBytes, oldProcess.Sample.SentBytes))
                // This is an unverified observation segment, not proof of a process lifetime.
                processId = oldProcess.Id;
            processId ??= Execute("""
                INSERT INTO process_instances(run_id,pid,started_at_utc_us) VALUES($p0,$p1,$p2)
                RETURNING process_instance_id
                """, runId, process.Pid, identity.StartedAtUtcUs);
            var pid = (long)processId!;
            var initialProcess = !continuous || oldProcess?.Id != pid;
            if (initialProcess) Execute("INSERT INTO process_presence_events VALUES($p0,$p1,1)", sampleId, pid);
            Execute("""
                INSERT INTO process_name_observations
                SELECT $p0,$p1,$p2 WHERE $p3 OR $p2 IS NOT (
                    SELECT source_name FROM process_name_observations
                    WHERE process_instance_id=$p1 ORDER BY sample_id DESC LIMIT 1)
                """, sampleId, pid, process.Name, initialProcess);
            Execute("""
                INSERT INTO process_path_observations
                SELECT $p0,$p1,$p2 WHERE $p3 OR $p2 IS NOT (
                    SELECT executable_path FROM process_path_observations
                    WHERE process_instance_id=$p1 ORDER BY sample_id DESC LIMIT 1)
                """, sampleId, pid, identity.ExecutablePath, initialProcess);
            Counters("process", pid, process.ReceivedBytes, process.SentBytes, initialProcess);
            nextProcesses.Add(process.Pid, new ActiveProcess(pid, process));

            foreach (var group in process.Sockets.GroupBy(socket => socket.Key))
            {
                var key = (pid, group.Key);
                // Duplicate tuples have no individual identity. Reuse only an identical
                // multiset, independent of nettop row order; any change starts new segments.
                var sockets = group.OrderBy(s => s.State, StringComparer.Ordinal)
                    .ThenBy(s => s.ReceivedBytes).ThenBy(s => s.SentBytes).ToList();
                activeSockets.TryGetValue(key, out var old);
                var reuse = continuous && !initialProcess && old?.Count == sockets.Count &&
                    (sockets.Count == 1
                        ? !Decreased(sockets[0].ReceivedBytes, sockets[0].SentBytes, old[0].Sample.ReceivedBytes, old[0].Sample.SentBytes)
                        : sockets.SequenceEqual(old.Select(s => s.Sample)));
                var current = new List<ActiveSocket>();
                foreach (var (socket, index) in sockets.Select((socket, index) => (socket, index)))
                {
                    var ambiguous = sockets.Count > 1 || identity.StartedAtUtcUs is null;
                    var id = reuse ? old![index].Id : (long)Execute("""
                        INSERT INTO socket_instances(process_instance_id,protocol,ip_version,local_address,local_port,
                            remote_address,remote_port,interface,ambiguous)
                        VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8) RETURNING socket_id
                        """, pid, socket.Key.Protocol, socket.Key.IpVersion, socket.Key.LocalAddress, socket.Key.LocalPort,
                        socket.Key.RemoteAddress, socket.Key.RemotePort, socket.Key.Interface, ambiguous)!;
                    if (!reuse) Execute("INSERT INTO socket_presence_events VALUES($p0,$p1,1)", sampleId, id);
                    Execute("""
                        INSERT INTO socket_state_observations
                        SELECT $p0,$p1,$p2 WHERE $p3 OR $p2 IS NOT (
                            SELECT state FROM socket_state_observations WHERE socket_id=$p1 ORDER BY sample_id DESC LIMIT 1)
                        """, sampleId, id, socket.State, !reuse);
                    Counters("socket", id, socket.ReceivedBytes, socket.SentBytes, !reuse);
                    current.Add(new ActiveSocket(id, socket));
                }
                nextSockets.Add(key, current);
            }
        }
        if (continuous)
        {
            foreach (var (pid, process) in activeProcesses)
                if (!nextProcesses.TryGetValue(pid, out var current) || current.Id != process.Id)
                    Execute("INSERT INTO process_presence_events VALUES($p0,$p1,0)", sampleId, process.Id);
            foreach (var (key, sockets) in activeSockets)
                foreach (var socket in sockets)
                    if (!nextSockets.TryGetValue(key, out var current) || current.All(s => s.Id != socket.Id))
                        Execute("INSERT INTO socket_presence_events VALUES($p0,$p1,0)", sampleId, socket.Id);
        }
        transaction.Commit();
        activeProcesses = nextProcesses;
        activeSockets = nextSockets;
        sequence++;
        previousTime = error is null ? observedAt : null;
        previousElapsed = error is null ? elapsed : null;
    }

    private static bool Decreased(long? received, long? sent, long? oldReceived, long? oldSent) =>
        received < oldReceived || sent < oldSent;

    internal void Stop(long stoppedAt, string reason)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE capture_runs SET stopped_at_utc_ms=$time,stop_reason=$reason WHERE run_id=$id";
        command.Parameters.AddWithValue("$time", stoppedAt);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$id", runId);
        command.ExecuteNonQuery();
    }

    public void Dispose() => connection.Dispose();
}
