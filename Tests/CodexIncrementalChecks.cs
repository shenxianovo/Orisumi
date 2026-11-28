#:project ../Collectors/Codex/Orisumi.Collectors.Codex.csproj
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property PublishAot=false
#:property AssemblyName=CodexIncrementalChecks

using System.Text.Json;
using System.Text;
using ZstdSharp;
using Microsoft.Data.Sqlite;
using Orisumi.Collectors.Codex;
using Orisumi.Core;

var root = Directory.CreateTempSubdirectory("orisumi-codex-incremental-").FullName;
var previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
Environment.SetEnvironmentVariable("CODEX_HOME", root);
try
{
    var store = Path.Combine(root, "store");
    const string filename = "rollout-2026-01-01T00-00-00-019b7a61-bc00-7111-8111-111111111111.jsonl";
    var sessions = Directory.CreateDirectory(Path.Combine(root, "sessions/2026/01/01")).FullName;
    var rollout = Path.Combine(sessions, filename);
    using var source = Open(Path.Combine(root, "state_5.sqlite"));
    Execute(source, """
        CREATE TABLE threads(id TEXT PRIMARY KEY,name TEXT,archived INT,is_pinned INT);
        INSERT INTO threads VALUES('t','A',0,0);
        """);
    const string meta = """
        {"type":"session_meta","ordinal":0,"payload":{"id":"t","session_id":"t","timestamp":"2026-01-01T00:00:00Z","cwd":"/work","source":"cli"}}
        """ + "\n";
    const string started = """
        {"type":"event_msg","ordinal":1,"payload":{"type":"task_started","turn_id":"turn","started_at":1767225600}}
        """ + "\n";
    File.WriteAllText(rollout, meta + started + Usage("first", 1) + "\n");
    CodexCollector.Capture(store);
    using var db = Open(new Store(store).CurrentDatabase("codex")!);
    // Replacing a source file must not discard a new response at the same source line.
    File.WriteAllText(rollout, meta + started + Usage("second", 2) + "\n");
    CodexCollector.Capture(store);
    Check(Scalar(db, "SELECT COUNT(*) FROM response_token_usage_events") == 2,
        "A rewritten source line discarded a distinct response");
    Console.WriteLine("PASS: rewritten rollout preserves distinct responses");
    var context = JsonSerializer.Serialize(new { type="turn_context", payload=new { turn_id="turn", model="model-a" } });
    File.AppendAllText(rollout, context + "\n");
    CodexCollector.Capture(store);
    var offset = Scalar(db, "SELECT byte_offset FROM _rollout_cursors");
    var appended = Usage("third", 3) + "\n";
    File.AppendAllText(rollout, appended);
    var appendResult = CodexCollector.Capture(store);
    Check(Scalar(db, "SELECT byte_offset FROM _rollout_cursors") == offset + Encoding.UTF8.GetByteCount(appended), "Append cursor has the wrong boundary");
    Check(appendResult.Contains($", {Encoding.UTF8.GetByteCount(appended)} rollout bytes read "), "Append parsed more than appended bytes");
    Check(Text(db, "SELECT model FROM response_token_usage WHERE response_id='third'") == "model-a", "Incremental read lost turn model context");
    Check(CodexCollector.Capture(store).Contains(", 0 rollout bytes read "), "Unchanged file was parsed again");
    Check(Scalar(db, "SELECT COUNT(*) FROM capture_coverage WHERE scope='rollout'") == 2, "Normal appends emitted coverage rows");

    var partial = Usage("partial", 4);
    File.AppendAllText(rollout, partial[..(partial.Length/2)]);
    CodexCollector.Capture(store);
    Check(Scalar(db,"SELECT COUNT(*) FROM response_token_usage WHERE response_id='partial'")==0,"Incomplete line was imported");
    File.AppendAllText(rollout, partial[(partial.Length/2)..]);
    CodexCollector.Capture(store);
    Check(Scalar(db,"SELECT COUNT(*) FROM response_token_usage WHERE response_id='partial'")==0,"Unterminated line advanced cursor");
    File.AppendAllText(rollout, "\n");
    CodexCollector.Capture(store);
    Check(Scalar(db,"SELECT COUNT(*) FROM response_token_usage WHERE response_id='partial'")==1,"Completed residual line was lost");
    Console.WriteLine("PASS: append-only reads, persisted context, unchanged files and residual lines");

    var repeated = """
        {"type":"event_msg","payload":{"type":"item_completed","thread_id":"t","turn_id":"turn","item":{"type":"FutureItem","id":"repeat"}}}
        """ + "\n";
    File.AppendAllText(rollout, repeated + repeated + Usage("third", 3) + "\n");
    CodexCollector.Capture(store);
    Check(Scalar(db, "SELECT COUNT(*) FROM rollout_events WHERE item_id='repeat'") == 2, "Identical source events collapsed");
    Check(Scalar(db, "SELECT COUNT(*) FROM response_token_usage_events WHERE response_id='third'") == 1, "Response usage duplicated");
    var twoCopies = File.ReadAllText(rollout);
    File.WriteAllText(rollout, twoCopies.Replace(repeated + repeated, repeated, StringComparison.Ordinal));
    CodexCollector.Capture(store);
    File.AppendAllText(rollout, repeated);
    CodexCollector.Capture(store);
    Check(Scalar(db, "SELECT COUNT(*) FROM rollout_events WHERE item_id='repeat'") == 2, "Truncate and append duplicated a previously read occurrence");
    File.AppendAllText(rollout, repeated);
    CodexCollector.Capture(store);
    Check(Scalar(db, "SELECT COUNT(*) FROM rollout_events WHERE item_id='repeat'") == 3, "A new identical occurrence was lost");
    offset = Scalar(db, "SELECT byte_offset FROM _rollout_cursors");
    var archiveDirectory = Directory.CreateDirectory(Path.Combine(root, "archived_sessions")).FullName;
    var renamed = Path.Combine(archiveDirectory, filename);
    File.Move(rollout, renamed);rollout=renamed;
    CodexCollector.Capture(store);
    File.AppendAllText(rollout,Usage("moved",6)+"\n");
    var moved = CodexCollector.Capture(store);
    Check(moved.Contains($", {Encoding.UTF8.GetByteCount(Usage("moved", 6) + "\n")} rollout bytes read "), "Rename lost the byte cursor");
    Check(Scalar(db, "SELECT byte_offset FROM _rollout_cursors") > offset, "Append after rename did not advance the cursor");
    Check(Scalar(db, "SELECT COUNT(*) FROM rollouts") == 1, "Move created a second rollout identity");
    Check(Scalar(db, "SELECT COUNT(*) FROM rollout_events WHERE typeof(record_hash)='blob' AND length(record_hash)=32") ==
        Scalar(db, "SELECT COUNT(*) FROM rollout_events"), "Deduplication hashes are not stored as 32-byte blobs");
    Check(Text(db, """
        SELECT source_path FROM response_token_usage_events u JOIN source_file_events f ON f.event_id=u.source_file_event_id
        WHERE response_id='first'
        """) == Path.Combine(sessions, filename), "Move changed the original event location");
    Check(Text(db, """
        SELECT source_path FROM response_token_usage_events u JOIN source_file_events f ON f.event_id=u.source_file_event_id
        WHERE response_id='moved' AND f.kind='moved'
        """) == renamed, "Append after move did not reference the new location");

    var plain=File.ReadAllBytes(rollout);
    var archived = renamed + ".zst";
    var archivedBytes = plain.Concat(Encoding.UTF8.GetBytes(Usage("archive-eof", 7))).ToArray();
    using (var compressor = new Compressor()) File.WriteAllBytes(archived, compressor.Wrap(archivedBytes).ToArray());
    File.Delete(rollout);
    rollout = archived;
    var usages = Scalar(db, "SELECT COUNT(*) FROM response_token_usage_events");
    var events = Scalar(db, "SELECT COUNT(*) FROM rollout_events");
    CodexCollector.Capture(store);
    Check(Scalar(db, "SELECT COUNT(*) FROM response_token_usage_events") == usages + 1, "Archive replay failed to deduplicate or skipped its final record");
    Check(Scalar(db, "SELECT COUNT(*) FROM response_token_usage WHERE response_id='archive-eof'") == 1, "Final archive record is missing");
    Check(Scalar(db, "SELECT COUNT(*) FROM rollout_events") == events, "Archive replay duplicated rollout events");
    Check(CodexCollector.Capture(store).Contains(", 0 rollout bytes read "), "Unchanged archive was decoded again");
    var archiveOffset = Scalar(db, "SELECT byte_offset FROM _rollout_cursors");
    var compressed = File.ReadAllBytes(archived);
    File.WriteAllBytes(archived, compressed[..^1]);
    try { CodexCollector.Capture(store); throw new Exception("Truncated archive accepted"); } catch (IOException) { }
    Check(Scalar(db, "SELECT byte_offset FROM _rollout_cursors") == archiveOffset, "Failed archive decode advanced the cursor");
    Check(Scalar(db, "SELECT COUNT(*) FROM capture_read_failures") == 1, "Archive decode failure was not recorded");
    var newVersion = archivedBytes.Concat(Encoding.UTF8.GetBytes("\n" + Usage("archive-v2", 8))).ToArray();
    using (var compressor = new Compressor()) File.WriteAllBytes(archived, compressor.Wrap(newVersion).ToArray());
    CodexCollector.Capture(store);
    Check(Scalar(db, "SELECT COUNT(*) FROM response_token_usage_events") == usages + 2, "New archive version lost or duplicated usage");
    Check(Scalar(db, "SELECT COUNT(*) FROM rollout_events") == events, "New archive version duplicated rollout events");
    Console.WriteLine("PASS: repeated events, rename continuation and compressed archive replay");

    File.Delete(archived);
    rollout = Path.Combine(sessions, filename);
    File.WriteAllBytes(rollout, plain);
    CodexCollector.Capture(store);
    offset=Scalar(db,"SELECT byte_offset FROM _rollout_cursors");
    var valid=Usage("atomic",7)+"\n";
    File.AppendAllText(rollout,valid+"{\"type\":\"token_usage_record\", invalid}\n");
    try{CodexCollector.Capture(store);throw new Exception("Malformed event accepted");}catch(IOException){}
    Check(Scalar(db,"SELECT byte_offset FROM _rollout_cursors")==offset,"Failed batch advanced cursor");
    Check(Scalar(db,"SELECT COUNT(*) FROM response_token_usage WHERE response_id='atomic'")==0,"Failed batch left partial facts");
    Check(Scalar(db,"SELECT COUNT(*) FROM capture_read_failures")==2,"Failure event rolled back");
    Check(Scalar(db,"SELECT COUNT(*) FROM capture_coverage WHERE evidence='read_failed'")==2,"Failure coverage missing");
    File.WriteAllBytes(rollout,plain.Concat(Encoding.UTF8.GetBytes(valid)).ToArray());
    CodexCollector.Capture(store);
    Check(Scalar(db,"SELECT COUNT(*) FROM response_token_usage WHERE response_id='atomic'")==1,"Retry did not recover committed boundary");
    File.WriteAllText(rollout,meta + started + Usage("truncated",8)+"\n");
    CodexCollector.Capture(store);
    Check(Scalar(db,"SELECT COUNT(*) FROM response_token_usage WHERE response_id='truncated'")==1,"Truncated file was not reread");
    var replace=rollout+".replacement";File.WriteAllText(replace,meta + started + Usage("replaced",9)+"\n");File.Move(replace,rollout,true);
    CodexCollector.Capture(store);
    Check(Scalar(db,"SELECT COUNT(*) FROM response_token_usage WHERE response_id='replaced'")==1,"Replacement lost new response");
    File.Delete(rollout);
    CodexCollector.Capture(store);CodexCollector.Capture(store);
    Check(Scalar(db,"SELECT COUNT(*) FROM source_file_events WHERE kind='missing'")==1,"Repeated missing source wrote duplicate state");
    File.WriteAllText(rollout,meta + started + Usage("restored",10)+"\n");
    CodexCollector.Capture(store);
    Check(Text(db,"SELECT kind FROM source_file_events ORDER BY event_id DESC LIMIT 1")=="replaced","Source restoration missing");
    Console.WriteLine("PASS: transactional cursor, failed attempt, truncation, replacement and missing-source recovery");

    // The changed record lies outside both of the former 4 KiB checkpoints.
    var padding = new string(' ', 8192) + "\n";
    var middle = meta + padding + Usage("middle-old", 10) + "\n" + padding;
    File.WriteAllText(rollout, middle);
    CodexCollector.Capture(store);
    File.WriteAllText(rollout, middle.Replace("middle-old", "middle-new", StringComparison.Ordinal) + Usage("after-middle", 10) + "\n");
    CodexCollector.Capture(store);
    Check(Scalar(db, "SELECT COUNT(*) FROM response_token_usage_events WHERE response_id IN ('middle-old','middle-new','after-middle')") == 3,
        "Rewrite in the middle followed by an append lost a response");
    Console.WriteLine("PASS: middle rewrite followed by append is replayed");

    using(var stop=new CancellationTokenSource())
    {
        var runsBeforeWatch = Scalar(db, "SELECT COUNT(*) FROM capture_runs");
        var watch=Task.Run(()=>CodexCollector.Watch(store,stop.Token));
        await Until(()=>Scalar(db,"SELECT COUNT(*) FROM capture_runs WHERE stopped_at_utc_ms IS NULL AND confirmed_until_utc_ms IS NOT NULL")==1);
        var runId = Scalar(db, "SELECT run_id FROM capture_runs WHERE stopped_at_utc_ms IS NULL");
        Execute(source,"UPDATE threads SET name='watched'");
        await Until(()=>Text(db,"SELECT name FROM current_threads")=="watched");
        File.AppendAllText(rollout,Usage("watch",11)+"\n");
        await Until(()=>Scalar(db,"SELECT COUNT(*) FROM response_token_usage WHERE response_id='watch'")==1);
        var watchedMiddle = File.ReadAllText(rollout).Replace("middle-new", "middle-now", StringComparison.Ordinal);
        File.WriteAllText(rollout, watchedMiddle + Usage("watch-middle-append", 11) + "\n");
        await Until(()=>Scalar(db,"SELECT COUNT(*) FROM response_token_usage WHERE response_id='watch-middle-append'")==1);
        Check(Scalar(db, "SELECT COUNT(*) FROM response_token_usage WHERE response_id='middle-now'") == 0,
            "Notification append replayed an unchecked middle rewrite before reconciliation");
        var beforeFailure=File.ReadAllBytes(rollout);
        var confirmed = Scalar(db, "SELECT confirmed_until_utc_ms FROM capture_runs WHERE stopped_at_utc_ms IS NULL");
        var failedBefore = Scalar(db, "SELECT COUNT(*) FROM capture_read_failures");
        File.AppendAllText(rollout,"{\"type\":\"token_usage_record\", invalid}\n");
        await Until(()=>Scalar(db,"SELECT COUNT(*) FROM capture_read_failures")>failedBefore);
        Check(Scalar(db, "SELECT confirmed_until_utc_ms FROM capture_runs WHERE stopped_at_utc_ms IS NULL") == confirmed,
            "Failed round advanced run confirmation");
        Check(!watch.IsCompleted,"Malformed source terminated watcher");
        File.WriteAllBytes(rollout,beforeFailure.Concat(Encoding.UTF8.GetBytes(Usage("repaired",12)+"\n")).ToArray());
        await Until(()=>Scalar(db,"SELECT COUNT(*) FROM response_token_usage WHERE response_id='repaired'")==1);
        var beforeNonObject=File.ReadAllBytes(rollout);
        failedBefore=Scalar(db,"SELECT COUNT(*) FROM capture_read_failures");
        File.AppendAllText(rollout,"[\"token_usage_record\"]\n");
        await Until(()=>Scalar(db,"SELECT COUNT(*) FROM capture_read_failures")>failedBefore);
        await Task.WhenAny(watch,Task.Delay(150));
        Check(!watch.IsCompleted,"Non-object JSON terminated watcher");
        File.WriteAllBytes(rollout,beforeNonObject.Concat(Encoding.UTF8.GetBytes(Usage("repaired-object",13)+"\n")).ToArray());
        await Until(()=>Scalar(db,"SELECT COUNT(*) FROM response_token_usage WHERE response_id='repaired-object'")==1);
        await Task.Delay(400);
        var beforeRename=Scalar(db,"SELECT confirmed_until_utc_ms FROM capture_runs WHERE stopped_at_utc_ms IS NULL");
        var unrelated=Path.Combine(root,"unrelated.tmp");
        File.WriteAllText(unrelated,"unrelated");
        File.Move(unrelated,unrelated+".renamed");
        await Task.Delay(500);
        Check(Scalar(db,"SELECT confirmed_until_utc_ms FROM capture_runs WHERE stopped_at_utc_ms IS NULL")==beforeRename,"Unrelated rename triggered capture");
        Check(Scalar(db, "SELECT COUNT(*) FROM capture_runs") == runsBeforeWatch + 1, "Watch rounds opened extra runs");
        Check(Scalar(db, $"SELECT COUNT(*) FROM capture_read_failures WHERE run_id={runId}") >= 2, "Failures did not point to the watching run");
        Check(Scalar(db, $"SELECT COUNT(*) FROM response_token_usage_events WHERE run_id={runId} AND response_id IN ('watch','repaired','repaired-object')") == 3,
            "Facts from separate rounds did not share the resident run");
        stop.Cancel();await watch.WaitAsync(TimeSpan.FromSeconds(5));
        Check(Text(db, $"SELECT stop_reason FROM capture_runs WHERE run_id={runId}") == "completed", "Cancellation did not close the run");
        CodexCollector.Capture(store);
        Check(Scalar(db, "SELECT COUNT(*) FROM response_token_usage WHERE response_id='middle-now'") == 1,
            "Full reconciliation lost the middle rewrite after committing an append");
    }
    Console.WriteLine("PASS: database/rollout notifications, malformed source recovery, failure evidence and cancellation");

    using (var stop = new CancellationTokenSource())
    {
        var runs = Scalar(db, "SELECT COUNT(*) FROM capture_runs");
        var watch = Task.Run(() => CodexCollector.Watch(store, stop.Token, TimeSpan.FromSeconds(2)));
        try
        {
            await Until(() => Scalar(db, "SELECT COUNT(*) FROM capture_runs") == runs + 1 &&
                Scalar(db, "SELECT COUNT(*) FROM capture_runs WHERE stopped_at_utc_ms IS NULL AND confirmed_until_utc_ms IS NOT NULL") == 1);
            var changed = File.ReadAllText(rollout).Replace("middle-now", "middle-due", StringComparison.Ordinal);
            File.WriteAllText(rollout, changed + Usage("before-periodic", 14) + "\n");
            await Until(() => Scalar(db, "SELECT COUNT(*) FROM response_token_usage WHERE response_id='before-periodic'") == 1);
            Check(Scalar(db, "SELECT COUNT(*) FROM response_token_usage WHERE response_id='middle-due'") == 0,
                "The append is committed before the next periodic reconciliation");
            for (var i = 0; i < 15; i++)
            {
                File.AppendAllText(rollout, " \n");
                await Task.Delay(200);
            }
            Check(Scalar(db, "SELECT COUNT(*) FROM response_token_usage WHERE response_id='middle-due'") == 1,
                "Continuous notifications must not postpone periodic reconciliation");
        }
        finally { stop.Cancel(); await watch.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
    Console.WriteLine("PASS: periodic full reconciliation replays a middle rewrite under continued append notifications");

    CheckStateSchema();

    void CheckStateSchema()
    {
        Execute(source, "ALTER TABLE threads RENAME COLUMN is_pinned TO changed_pin");
        File.AppendAllText(rollout, Usage("state-schema-changed", 15) + "\n");
        CodexCollector.Capture(store);
        Check(Scalar(db, "SELECT COUNT(*) FROM response_token_usage WHERE response_id='state-schema-changed'") == 1,
            "A changed state_5 schema blocked rollout facts");
        Check(Scalar(db, "SELECT COUNT(*) FROM capture_coverage WHERE scope='current_state' AND evidence='read_failed' AND detail LIKE '%is_pinned%'") == 1,
            "state_5 failure is recorded with its own scope");
        Execute(source, "ALTER TABLE threads RENAME COLUMN changed_pin TO is_pinned; UPDATE threads SET is_pinned=1");
        CodexCollector.Capture(store);
        Check(Scalar(db, "SELECT is_pinned FROM current_threads WHERE thread_id='t'") == 1, "state_5 reads resume after schema repair");
        Console.WriteLine("PASS: state_5 schema failures leave scoped evidence and rollout facts keep committing");
    }

    var replacementHome=Path.Combine(root,"recreated-source");
    var replacementStore=Path.Combine(root,"replacement-store");
    void CreateSource(string title)
    {
        Directory.CreateDirectory(replacementHome);
        using var stateCopy=Open(Path.Combine(replacementHome,"state_5.sqlite"));
        source.BackupDatabase(stateCopy);
        Execute(stateCopy,"UPDATE threads SET name=$p0",title);
        var recreatedSessions = Directory.CreateDirectory(Path.Combine(replacementHome, "sessions"));
        File.Copy(rollout, Path.Combine(recreatedSessions.FullName, filename));
    }
    CreateSource("before-recreation");
    Environment.SetEnvironmentVariable("CODEX_HOME",replacementHome);
    using(var stop=new CancellationTokenSource())
    {
        var watch=Task.Run(()=>CodexCollector.Watch(replacementStore,stop.Token));
        try
        {
            await Until(()=>new Store(replacementStore).CurrentDatabase("codex") is not null);
            using var replacementDb=Open(new Store(replacementStore).CurrentDatabase("codex")!);
            await Until(()=>Text(replacementDb,"SELECT name FROM current_threads")=="before-recreation");
            Directory.Move(replacementHome,replacementHome+".old");
            CreateSource("after-recreation");
            await Until(()=>Text(replacementDb,"SELECT name FROM current_threads")=="after-recreation");
            using var recreatedState=Open(Path.Combine(replacementHome,"state_5.sqlite"));
            Execute(recreatedState,"UPDATE threads SET name='after-rebind'");
            await Until(()=>Text(replacementDb,"SELECT name FROM current_threads")=="after-rebind");
        }
        finally { stop.Cancel(); await watch.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
    Console.WriteLine("PASS: recreated source directory restores database notifications");

}
finally
{
    Environment.SetEnvironmentVariable("CODEX_HOME", previousHome);
    Directory.Delete(root, true);
}

static string Usage(string response, int output) => JsonSerializer.Serialize(new {
    type = "token_usage_record", timestamp = "2026-01-01T00:00:00Z",
    payload = new { thread_id = "t", turn_id = "turn", response_id = response,
        usage = new { input_tokens = 10, output_tokens = output } }
});
static SqliteConnection Open(string file)
{
    var connection = new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=file,Pooling=false}.ToString());
    connection.Open(); return connection;
}
static void Execute(SqliteConnection db, string sql, params object?[] parameters)
{
    using var command = db.CreateCommand(); command.CommandText=sql;
    for(var i=0;i<parameters.Length;i++)command.Parameters.AddWithValue($"$p{i}",parameters[i]??DBNull.Value);
    command.ExecuteNonQuery();
}
static long Scalar(SqliteConnection db,string sql)
{
    using var command=db.CreateCommand();command.CommandText=sql;return Convert.ToInt64(command.ExecuteScalar());
}
static void Check(bool condition,string message){if(!condition)throw new Exception(message);}

static string? Text(SqliteConnection db,string sql){using var c=db.CreateCommand();c.CommandText=sql;return c.ExecuteScalar() as string;}
static async Task Until(Func<bool> condition){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));while(!condition())await Task.Delay(50,timeout.Token);}
