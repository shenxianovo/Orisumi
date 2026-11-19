#:project ../src/Collectors/Orisumi.Collectors.Codex/Orisumi.Collectors.Codex.csproj
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property PublishAot=false

using System.Text.Json;
using System.Text;
using ZstdSharp;
using Microsoft.Data.Sqlite;
using Orisumi.Collectors.Codex;
using Orisumi.Platform;

var root = Directory.CreateTempSubdirectory("orisumi-codex-incremental-").FullName;
var previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
Environment.SetEnvironmentVariable("CODEX_HOME", root);
try
{
    var store = Path.Combine(root, "store");
    var rollout = Path.Combine(root, "rollout.jsonl");
    using var source = Open(Path.Combine(root, "state_5.sqlite"));
    Execute(source, """
        CREATE TABLE threads(id TEXT,name TEXT,title TEXT,cwd TEXT,created_at_ms INT,created_at INT,archived INT,history_mode TEXT,rollout_path TEXT);
        INSERT INTO threads VALUES('t','A','fallback','/work',1000,1,0,'paginated',$p0);
        """, rollout);
    using var history = Open(Path.Combine(root, "thread_history_1.sqlite"));
    Execute(history, """
        CREATE TABLE thread_turns(thread_id TEXT,turn_id TEXT,started_at INT,completed_at INT,status TEXT);
        CREATE TABLE thread_items(thread_id TEXT,turn_id TEXT,item_id TEXT,item_type TEXT,created_at_ms INT,rollout_ordinal INT,item_json TEXT);
        """);
    File.WriteAllText(rollout, Usage("first", 1) + "\n");
    CodexCollector.Capture(store);
    using var db = Open(new Store(store).CurrentDatabase("codex")!);
    // Replacing a source file must not discard a new response at the same source line.
    File.WriteAllText(rollout, Usage("second", 2) + "\n");
    CodexCollector.Capture(store);
    Check(Scalar(db, "SELECT COUNT(*) FROM response_token_usage_observations") == 2,
        "A rewritten source line discarded a distinct response");
    Console.WriteLine("PASS: rewritten rollout preserves distinct responses");
    var context = JsonSerializer.Serialize(new { type="turn_context", payload=new { turn_id="turn", model="model-a" } });
    File.AppendAllText(rollout, context + "\n");
    CodexCollector.Capture(store);
    var offset = Scalar(db, "SELECT byte_offset FROM _token_usage_rollouts");
    var appended = Usage("third", 3) + "\n";
    File.AppendAllText(rollout, appended);
    CodexCollector.Capture(store);
    Check(Scalar(db, "SELECT start_offset FROM usage_reads ORDER BY read_id DESC LIMIT 1") == offset, "Append re-read committed prefix");
    Check(Scalar(db, "SELECT bytes_read FROM usage_reads ORDER BY read_id DESC LIMIT 1") == Encoding.UTF8.GetByteCount(appended), "Append parsed more than appended bytes");
    Check(Text(db, "SELECT model FROM response_token_usage WHERE response_id='third'") == "model-a", "Incremental read lost turn model context");
    var readCount=Scalar(db,"SELECT COUNT(*) FROM usage_reads");
    CodexCollector.Capture(store);
    Check(Scalar(db,"SELECT COUNT(*) FROM usage_reads")==readCount,"Unchanged file was parsed again");

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

    var repeated = Usage("third", 3);
    File.AppendAllText(rollout, repeated+"\n"+Usage("third",5)+"\n"+repeated+"\n");
    CodexCollector.Capture(store);
    Check(Scalar(db,"SELECT COUNT(*) FROM response_token_usage_observations WHERE response_id='third'")==4,"Identical repeated source events collapsed");
    Check(Scalar(db,"SELECT total_output_tokens FROM response_token_usage WHERE response_id='third'")==3,"A-B-A source ordering was lost");
    offset = Scalar(db,"SELECT byte_offset FROM _token_usage_rollouts");
    var renamed = Path.Combine(root,"moved.jsonl");
    File.Move(rollout, renamed);rollout=renamed;
    Execute(source,"UPDATE threads SET rollout_path=$p0",rollout);
    CodexCollector.Capture(store);
    File.AppendAllText(rollout,Usage("moved",6)+"\n");
    CodexCollector.Capture(store);
    Check(Scalar(db,"SELECT start_offset FROM usage_reads ORDER BY read_id DESC LIMIT 1")==offset,"Rename lost the byte cursor");

    var plain=File.ReadAllBytes(rollout);
    var archived=rollout+".zst";
    using(var compressor=new Compressor())File.WriteAllBytes(archived,compressor.Wrap(plain).ToArray());
    File.Delete(rollout);rollout=archived;
    Execute(source,"UPDATE threads SET rollout_path=$p0",rollout);
    var usages=Scalar(db,"SELECT COUNT(*) FROM response_token_usage_observations");
    CodexCollector.Capture(store);
    Check(Scalar(db,"SELECT COUNT(*) FROM response_token_usage_observations")==usages,"Compression replay duplicated usage");
    readCount=Scalar(db,"SELECT COUNT(*) FROM usage_reads");
    CodexCollector.Capture(store);
    Check(Scalar(db,"SELECT COUNT(*) FROM usage_reads")==readCount,"Unchanged archive was decoded again");
    Console.WriteLine("PASS: repeated events, rename continuation and compressed archive replay");

    rollout=Path.Combine(root,"active.jsonl");
    File.WriteAllBytes(rollout,plain);Execute(source,"UPDATE threads SET rollout_path=$p0",rollout);
    CodexCollector.Capture(store);
    offset=Scalar(db,"SELECT byte_offset FROM _token_usage_rollouts");
    var valid=Usage("atomic",7)+"\n";
    File.AppendAllText(rollout,valid+"{\"type\":\"token_usage_record\", invalid}\n");
    try{CodexCollector.Capture(store);throw new Exception("Malformed event accepted");}catch(IOException){}
    Check(Scalar(db,"SELECT byte_offset FROM _token_usage_rollouts")==offset,"Failed batch advanced cursor");
    Check(Scalar(db,"SELECT COUNT(*) FROM response_token_usage WHERE response_id='atomic'")==0,"Failed batch left partial facts");
    Check(Scalar(db,"SELECT COUNT(*) FROM capture_runs WHERE error IS NOT NULL AND stopped_at_utc_ms IS NOT NULL")==1,"Failure attempt rolled back");
    Check(Scalar(db,"SELECT COUNT(*) FROM capture_coverage WHERE evidence='failed'")==1,"Failure coverage missing");
    File.WriteAllBytes(rollout,plain.Concat(Encoding.UTF8.GetBytes(valid)).ToArray());
    CodexCollector.Capture(store);
    Check(Scalar(db,"SELECT COUNT(*) FROM response_token_usage WHERE response_id='atomic'")==1,"Retry did not recover committed boundary");
    File.WriteAllText(rollout,Usage("truncated",8)+"\n");
    CodexCollector.Capture(store);
    Check(Scalar(db,"SELECT COUNT(*) FROM response_token_usage WHERE response_id='truncated'")==1,"Truncated file was not reread");
    var replace=rollout+".replacement";File.WriteAllText(replace,Usage("replaced",9)+"\n");File.Move(replace,rollout,true);
    CodexCollector.Capture(store);
    Check(Scalar(db,"SELECT COUNT(*) FROM response_token_usage WHERE response_id='replaced'")==1,"Replacement lost new response");
    File.Delete(rollout);
    CodexCollector.Capture(store);CodexCollector.Capture(store);
    Check(Scalar(db,"SELECT COUNT(*) FROM usage_source_events WHERE available=0")==1,"Repeated missing source wrote duplicate state");
    File.WriteAllText(rollout,Usage("restored",10)+"\n");
    CodexCollector.Capture(store);
    Check(Scalar(db,"SELECT available FROM usage_source_events ORDER BY event_id DESC LIMIT 1")==1,"Source restoration missing");
    Console.WriteLine("PASS: transactional cursor, failed attempt, truncation, replacement and missing-source recovery");

    using(var stop=new CancellationTokenSource())
    {
        var watch=Task.Run(()=>CodexCollector.Watch(store,stop.Token));
        await Until(()=>Scalar(db,"SELECT COUNT(*) FROM capture_runs WHERE trigger='startup' AND stopped_at_utc_ms IS NOT NULL")>0);
        Execute(source,"UPDATE threads SET name='watched'");
        await Until(()=>Text(db,"SELECT title FROM current_threads")=="watched");
        File.AppendAllText(rollout,Usage("watch",11)+"\n");
        await Until(()=>Scalar(db,"SELECT COUNT(*) FROM response_token_usage WHERE response_id='watch'")==1);
        var beforeFailure=File.ReadAllBytes(rollout);
        File.AppendAllText(rollout,"{\"type\":\"token_usage_record\", invalid}\n");
        await Until(()=>Scalar(db,"SELECT COUNT(*) FROM capture_runs WHERE error IS NOT NULL")>=2);
        Check(!watch.IsCompleted,"Malformed source terminated watcher");
        File.WriteAllBytes(rollout,beforeFailure.Concat(Encoding.UTF8.GetBytes(Usage("repaired",12)+"\n")).ToArray());
        await Until(()=>Scalar(db,"SELECT COUNT(*) FROM response_token_usage WHERE response_id='repaired'")==1);
        var beforeNonObject=File.ReadAllBytes(rollout);
        var failedBefore=Scalar(db,"SELECT COUNT(*) FROM capture_runs WHERE error IS NOT NULL");
        File.AppendAllText(rollout,"[\"token_usage_record\"]\n");
        await Until(()=>Scalar(db,"SELECT COUNT(*) FROM capture_runs WHERE error IS NOT NULL")>failedBefore);
        await Task.WhenAny(watch,Task.Delay(150));
        Check(!watch.IsCompleted,"Non-object JSON terminated watcher");
        File.WriteAllBytes(rollout,beforeNonObject.Concat(Encoding.UTF8.GetBytes(Usage("repaired-object",13)+"\n")).ToArray());
        await Until(()=>Scalar(db,"SELECT COUNT(*) FROM response_token_usage WHERE response_id='repaired-object'")==1);
        await Task.Delay(400);
        var runsBeforeRename=Scalar(db,"SELECT COUNT(*) FROM capture_runs");
        var unrelated=Path.Combine(root,"unrelated.tmp");
        File.WriteAllText(unrelated,"unrelated");
        File.Move(unrelated,unrelated+".renamed");
        await Task.Delay(500);
        Check(Scalar(db,"SELECT COUNT(*) FROM capture_runs")==runsBeforeRename,"Unrelated rename triggered capture");
        stop.Cancel();await watch.WaitAsync(TimeSpan.FromSeconds(5));
    }
    Check(Scalar(db,"SELECT COUNT(*) FROM capture_runs WHERE trigger='change'")>=1,"Source notifications did not trigger capture: "+Text(db,"SELECT group_concat(trigger||':'||coalesce(error,''),' | ') FROM capture_runs"));
    Check(Scalar(db,"SELECT COUNT(*) FROM capture_coverage WHERE evidence='watch_gap' AND stopped_at_utc_ms IS NOT NULL")>=1,"Startup gap missing restored snapshot");
    Console.WriteLine("PASS: database/rollout notifications, malformed source recovery, gap evidence and cancellation");

    var replacementHome=Path.Combine(root,"recreated-source");
    var replacementStore=Path.Combine(root,"replacement-store");
    void CreateSource(string title)
    {
        Directory.CreateDirectory(replacementHome);
        using var stateCopy=Open(Path.Combine(replacementHome,"state_5.sqlite"));
        source.BackupDatabase(stateCopy);
        Execute(stateCopy,"UPDATE threads SET name=$p0",title);
        using var historyCopy=Open(Path.Combine(replacementHome,"thread_history_1.sqlite"));
        history.BackupDatabase(historyCopy);
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
            await Until(()=>Text(replacementDb,"SELECT title FROM current_threads")=="before-recreation");
            Directory.Move(replacementHome,replacementHome+".old");
            CreateSource("after-recreation");
            await Until(()=>Text(replacementDb,"SELECT title FROM current_threads")=="after-recreation");
            using var recreatedState=Open(Path.Combine(replacementHome,"state_5.sqlite"));
            Execute(recreatedState,"UPDATE threads SET name='after-rebind'");
            await Until(()=>Text(replacementDb,"SELECT title FROM current_threads")=="after-rebind");
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
