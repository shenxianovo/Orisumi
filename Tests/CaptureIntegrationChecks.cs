#:project ../Collectors/Claude/Orisumi.Collectors.Claude.csproj
#:project ../Collectors/Codex/Orisumi.Collectors.Codex.csproj
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Orisumi.Collectors.Claude;
using Orisumi.Collectors.Codex;
using Orisumi.Platform;

var root = Directory.GetParent(Path.GetDirectoryName(SourceFile())!)!.FullName;
var claude = Path.Combine(root, "Collectors/Claude/bin/Debug/net11.0/orisumi-claude.dll");
CodexChecks();
await ClaudeChecks();
await ResidentRunChecks();
Console.WriteLine("Codex and Claude capture integration checks passed.");

static string SourceFile([CallerFilePath] string path = "") => path;
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static void Exec(SqliteConnection db, string sql, params object?[] values)
{
    using var command = db.CreateCommand(); command.CommandText = sql;
    for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue($"$p{i}", values[i] ?? DBNull.Value);
    command.ExecuteNonQuery();
}
static void Rows(SqliteConnection db, string sql, params object?[][] expected)
{
    using var command = db.CreateCommand(); command.CommandText = sql;
    using var reader = command.ExecuteReader();
    var index = 0;
    while (reader.Read())
    {
        Check(index < expected.Length && reader.FieldCount == expected[index].Length, $"Unexpected row shape: {sql}");
        for (var column = 0; column < reader.FieldCount; column++)
        {
            var actual = reader.IsDBNull(column) ? null : Convert.ToString(reader.GetValue(column), CultureInfo.InvariantCulture);
            var value = expected[index][column] is null ? null : Convert.ToString(expected[index][column], CultureInfo.InvariantCulture);
            Check(actual == value, $"Unexpected row {index}, column {column}: {sql}; expected {value ?? "null"}, got {actual ?? "null"}");
        }
        index++;
    }
    Check(index == expected.Length, $"Missing rows: {sql}");
}
static void Count(SqliteConnection db, string table, int expected) => Rows(db, $"SELECT COUNT(*) FROM {table}", [expected]);
static SqliteConnection Open(string path)
{
    var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
    db.Open();
    return db;
}
// One capture round in process; the collectors read their source location from the environment.
static void Capture(Fixture fixture, Func<string, string> capture)
{
    Environment.SetEnvironmentVariable("CODEX_HOME", fixture.Root);
    Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", fixture.Root);
    try { capture(fixture.Store); }
    finally
    {
        Environment.SetEnvironmentVariable("CODEX_HOME", null);
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);
    }
}
void CodexChecks()
{
    const string main = "019b7a61-bc00-7111-8111-111111111111";
    const string fork = "019b7a61-bc00-7222-8222-222222222222";
    const string agent = "019b7a61-bc00-7333-8333-333333333333";
    const string segment = "019b7a61-bc00-7444-8444-444444444444";
    using var fixture = new Fixture();
    var sessions = Directory.CreateDirectory(Path.Combine(fixture.Root, "sessions/2026/01/01")).FullName;
    var archives = Directory.CreateDirectory(Path.Combine(fixture.Root, "archived_sessions")).FullName;
    var rollout = Path.Combine(sessions, $"rollout-2026-01-01T00-00-00-{main}.jsonl");
    var rows = """
        {"type":"session_meta","payload":{"id":"MAIN","session_id":"MAIN","timestamp":"2026-01-01T00:00:00Z","cwd":"/work","source":"cli","originator":"codex_cli_rs","git":{"commit_hash":"abc","branch":"main","repository_url":"https://example.test/repo"}}}
        {"type":"event_msg","payload":{"type":"task_started","turn_id":"turn","started_at":1767225600}}
        {"type":"response_item","payload":{"type":"message","role":"developer","content":[{"type":"input_text","text":"Skipped instructions"}]}}
        {"type":"turn_context","payload":{"turn_id":"turn","model":"model-a","cwd":"/work","approval_policy":"on-request","sandbox_policy":{"type":"read-only"},"effort":"high"}}
        {"type":"event_msg","payload":{"type":"item_completed","thread_id":"MAIN","turn_id":"turn","item":{"type":"UserMessage","id":"u","content":[{"type":"text","text":"Check the file.","text_elements":[]}]},"started_at_ms":1767225600123,"completed_at_ms":1767225600124}}
        {"type":"event_msg","payload":{"type":"item_completed","thread_id":"MAIN","turn_id":"turn","item":{"type":"CommandExecution","id":"ok","command":["cat","example.txt"],"cwd":"/work","aggregated_output":"keep out","exit_code":0,"duration":{"secs":0,"nanos":1000}}}}
        {"type":"event_msg","payload":{"type":"item_completed","thread_id":"MAIN","turn_id":"turn","item":{"type":"CommandExecution","id":"bad","command":["cat","missing"],"cwd":"/work","aggregated_output":"file missing","exit_code":1,"duration":{"secs":1,"nanos":0}}}}
        {"type":"event_msg","payload":{"type":"item_completed","thread_id":"MAIN","turn_id":"turn","item":{"type":"FileChange","id":"patch","changes":{"/work/example.txt":{"type":"update","unified_diff":"skip the diff","move_path":"/work/renamed.txt"},"/work/new.txt":{"type":"add","unified_diff":"skip the diff"}}}}}
        {"type":"event_msg","payload":{"type":"item_completed","thread_id":"MAIN","turn_id":"turn","item":{"type":"McpToolCall","id":"mcp","server":"test","tool":"read","arguments":{"id":1},"status":"completed","result":{"content":[]}}}}
        {"type":"event_msg","payload":{"type":"item_completed","thread_id":"MAIN","turn_id":"turn","item":{"type":"Reasoning","id":"r","summary_text":["skip"],"raw_content":["skip"]}}}
        {"type":"event_msg","payload":{"type":"item_completed","thread_id":"MAIN","turn_id":"turn","item":{"type":"FutureItem","id":"future","data":"skip"}}}
        {"type":"event_msg","payload":{"type":"item_completed","thread_id":"MAIN","turn_id":"turn","item":{"type":"Extension","kind":"web.search","id":"search","query":"example","action":{"type":"search","query":null,"queries":["example","another query"]},"results":[]}}}
        {"type":"event_msg","payload":{"type":"item_completed","thread_id":"MAIN","turn_id":"turn","item":{"type":"AgentMessage","id":"a","content":[{"type":"text","text":"Checked."}],"phase":"final_answer"}}}
        {"type":"token_usage_record","payload":{"thread_id":"MAIN","session_id":"MAIN","turn_id":"turn","response_id":"response","usage":{"input_tokens":10,"cached_input_tokens":2,"cache_write_input_tokens":1,"output_tokens":5,"reasoning_output_tokens":2,"total_tokens":15}}}
        {"type":"event_msg","payload":{"type":"task_complete","turn_id":"turn","started_at":1767225600,"completed_at":1767225603,"last_agent_message":"Checked."}}
        {"type":"response_item","payload":{"type":"web_search_call","id":"legacy-search","status":"completed","action":{"type":"search","query":"legacy query","queries":null},"internal_chat_message_metadata_passthrough":{"turn_id":"turn"}}}
        {"type":"event_msg","payload":{"type":"item_completed","thread_id":"MAIN","turn_id":"turn","item":{"type":"WebSearch","id":"direct-search","query":"direct query","action":{"type":"search","queries":["direct query"]}}}}
        {"type":"response_item","payload":{"type":"function_call","id":"sleep","name":"sleep","namespace":"clock","arguments":"{\"duration_ms\":1500}","call_id":"sleep","internal_chat_message_metadata_passthrough":{"turn_id":"turn"}}}
        {"type":"event_msg","payload":{"type":"item_completed","thread_id":"MAIN","turn_id":"turn","item":{"type":"Extension","kind":"clock.sleep","id":"sleep","durationMs":1500},"started_at_ms":1767225601000,"completed_at_ms":1767225602500}}
        {"type":"event_msg","payload":{"type":"item_completed","thread_id":"MAIN","turn_id":"turn","item":{"type":"Extension","kind":"image_gen.generation","id":"image","status":"completed","revisedPrompt":"A blue square","result":"AA==","transparentBackground":false,"failure":null,"savedPath":"/tmp/image.png"},"completed_at_ms":1767225602800}}
        """.Replace("MAIN", main, StringComparison.Ordinal);
    File.WriteAllText(rollout, Stamp(rows));
    File.WriteAllText(Path.Combine(sessions, $"rollout-2026-01-01T00-00-04-{main}_{fork}.jsonl"), Stamp("""
        {"type":"session_meta","payload":{"id":"FORK","session_id":"FORK","timestamp":"2026-01-01T00:00:04Z","cwd":"/work","source":"cli"}}
        {"type":"event_msg","payload":{"type":"task_started","turn_id":"fork-turn","started_at":1767225604}}
        {"type":"turn_context","payload":{"turn_id":"fork-turn","model":"model-b","cwd":"/work","approval_policy":"never","sandbox_policy":{"type":"read-only"}}}
        {"type":"event_msg","payload":{"type":"turn_aborted","turn_id":"fork-turn","reason":"interrupted","started_at":1767225604,"completed_at":1767225605}}
        """.Replace("FORK", fork, StringComparison.Ordinal)));
    File.WriteAllText(Path.Combine(archives, $"rollout-2026-01-01T00-00-04-{main}_{segment}.jsonl"), Stamp("""
        {"type":"session_meta","payload":{"id":"MAIN","session_id":"MAIN","timestamp":"2026-01-01T00:00:00Z","cwd":"/work","source":"cli"}}
        {"type":"event_msg","payload":{"type":"task_started","turn_id":"resumed-turn","started_at":1767225604}}
        {"type":"event_msg","payload":{"type":"item_completed","thread_id":"MAIN","turn_id":"resumed-turn","item":{"type":"UserMessage","id":"resumed","content":[{"type":"text","text":"Continue."}]}}}
        """.Replace("MAIN", main, StringComparison.Ordinal)));
    using (var compressor = new ZstdSharp.Compressor())
        File.WriteAllBytes(Path.Combine(archives, $"rollout-2026-01-01T00-00-05-{agent}.jsonl.zst"),
            compressor.Wrap(System.Text.Encoding.UTF8.GetBytes(Stamp("""
                {"type":"session_meta","payload":{"id":"AGENT","session_id":"AGENT","timestamp":"2026-01-01T00:00:05Z","cwd":"/work","source":{"subagent":{"thread_spawn":{"parent_thread_id":"MAIN","depth":1}}}}}
                {"type":"session_meta","payload":{"id":"MAIN","session_id":"MAIN","timestamp":"2026-01-01T00:00:00Z","cwd":"/work","source":"cli"}}
                {"type":"event_msg","payload":{"type":"task_started","turn_id":"agent-turn","started_at":1767225605}}
                {"type":"event_msg","payload":{"type":"item_completed","thread_id":"AGENT","turn_id":"agent-turn","item":{"type":"AgentMessage","id":"agent-a","content":[{"type":"text","text":"Done."}]}}}
                """.Replace("MAIN", main, StringComparison.Ordinal).Replace("AGENT", agent, StringComparison.Ordinal)).TrimEnd())).ToArray());
    using var state = Open(Path.Combine(fixture.Root, "state_5.sqlite"));
    Exec(state, """
        CREATE TABLE threads(id TEXT PRIMARY KEY,name TEXT,title TEXT,archived INT,is_pinned INT,history_mode TEXT);
        INSERT INTO threads VALUES($p0,'A','First prompt',0,0,'legacy');
        INSERT INTO threads VALUES('state-only','Ignored','ignored',0,0,'paginated');
        """, main);
    File.WriteAllText(Path.Combine(fixture.Root, "thread_history_1.sqlite"), "not a database");
    Capture(fixture, CodexCollector.Capture);
    using var db = Open(fixture.Database("codex"));
    Capture(fixture, CodexCollector.Capture);
    Exec(state, "UPDATE threads SET name='B',archived=1 WHERE id=$p0", main);
    Capture(fixture, CodexCollector.Capture);
    Exec(state, "UPDATE threads SET name=NULL,is_pinned=1 WHERE id=$p0", main);
    Capture(fixture, CodexCollector.Capture);
    Rows(db, "SELECT name FROM thread_name_observations ORDER BY observation_id", ["A"], ["B"], [""]);
    Count(db, "thread_archive_observations", 2);
    Count(db, "thread_pin_observations", 2);
    Count(db, "database_observations", 3);
    Rows(db, "SELECT thread_id,created_at_utc_ms,cwd,git_branch,git_sha,git_origin_url,name,archived,is_pinned FROM current_threads WHERE git_sha IS NOT NULL",
        [main, 1767225600000L, "/work", "main", "abc", "https://example.test/repo", "", 1, 1]);
    Rows(db, "SELECT thread_id,parent_thread_id,name FROM current_threads ORDER BY thread_id", [main, null, ""], [fork, main, null], [agent, main, null]);
    Rows(db, "SELECT thread_id,turn_id,status,started_at_utc_ms,completed_at_utc_ms FROM current_turns ORDER BY turn_id",
        [agent, "agent-turn", "inProgress", 1767225605000L, null], [fork, "fork-turn", "aborted", 1767225604000L, 1767225605000L],
        [main, "resumed-turn", "inProgress", 1767225604000L, null], [main, "turn", "completed", 1767225600000L, 1767225603000L]);
    Rows(db, "SELECT role,text,started_at_utc_ms,completed_at_utc_ms,rollout_ordinal,source_line FROM message_timeline WHERE turn_id='turn' ORDER BY source_line",
        ["user", "Check the file.", 1767225600123L, 1767225600124L, 4, 5], ["assistant", "Checked.", null, null, 12, 13]);
    Rows(db, "SELECT count(*) FROM message_timeline WHERE turn_id='resumed-turn'", [1]);
    Rows(db, "SELECT thread_id,text FROM message_timeline WHERE item_id='agent-a'", [agent, "Done."]);
    Rows(db, """
        SELECT item_id,json_extract(payload_json,'$.output_length'),json_extract(payload_json,'$.aggregated_output'),
            json_extract(payload_json,'$.duration.secs') FROM rollout_events JOIN event_contents USING(event_id)
        WHERE item_type='CommandExecution' ORDER BY source_line
        """, ["ok", 8, null, 0], ["bad", 12, "file missing", 1]);
    Rows(db, """
        SELECT item_id,json_extract(payload_json,'$.changes."/work/example.txt".type'),
            json_extract(payload_json,'$.changes."/work/example.txt".move_path'),
            json_extract(payload_json,'$.changes."/work/example.txt".unified_diff')
        FROM rollout_events JOIN event_contents USING(event_id) WHERE item_type='FileChange'
        """, ["patch", "update", "/work/renamed.txt", null]);
    Rows(db, """
        SELECT json_extract(payload_json,'$.server'),json_extract(payload_json,'$.tool'),json_extract(payload_json,'$.arguments.id'),
            json_extract(payload_json,'$.status'),json_extract(payload_json,'$.result')
        FROM rollout_events JOIN event_contents USING(event_id) WHERE item_type='McpToolCall'
        """, ["test", "read", 1, "completed", null]);
    Rows(db, """
        SELECT kind,turn_id,json_extract(payload_json,'$.query'),json_extract(payload_json,'$.queries[1]'),
            json_extract(payload_json,'$.status'),json_extract(payload_json,'$.results')
        FROM rollout_events JOIN event_contents USING(event_id) WHERE item_type='WebSearch' ORDER BY source_line
        """, ["item_completed", "turn", "example", "another query", "completed", null],
        ["web_search_call", "turn", "legacy query", null, "completed", null],
        ["item_completed", "turn", "direct query", null, "completed", null]);
    Rows(db, """
        SELECT kind,item_type,started_at_utc_ms,completed_at_utc_ms,json_extract(payload_json,'$.duration_ms')
        FROM rollout_events JOIN event_contents USING(event_id) WHERE item_id='sleep'
        """, ["item_completed", "Sleep", 1767225601000L, 1767225602500L, 1500]);
    Rows(db, """
        SELECT item_type,json_extract(payload_json,'$.revised_prompt'),json_extract(payload_json,'$.status'),
            json_extract(payload_json,'$.result'),json_extract(payload_json,'$.savedPath')
        FROM rollout_events JOIN event_contents USING(event_id) WHERE item_id='image'
        """, ["ImageGeneration", "A blue square", "completed", null, null]);
    Rows(db, """
        SELECT item_type,payload_json FROM rollout_events LEFT JOIN event_contents USING(event_id)
        WHERE item_id IN ('r','future') ORDER BY source_line
        """, ["Reasoning", null], ["FutureItem", null]);
    Rows(db, """
        SELECT json_extract(payload_json,'$.model'),json_extract(payload_json,'$.approval_policy'),json_extract(payload_json,'$.sandbox_policy.type'),
            json_extract(payload_json,'$.effort') FROM rollout_events JOIN event_contents USING(event_id)
        WHERE turn_id='turn' AND kind='turn_context'
        """, ["model-a", "on-request", "read-only", null]);
    Rows(db, """
        SELECT kind,model,total_input_tokens,cache_read_input_tokens,cache_write_input_tokens,total_output_tokens,reasoning_output_tokens,total_tokens
        FROM response_token_usage
        """, ["token_usage_record", "model-a", 10, 2, 1, 5, 2, 15]);
    Count(db, "rollout_events", 23);
    Rows(db, "SELECT stop_reason,error,confirmed_until_utc_ms IS NOT NULL FROM capture_runs GROUP BY stop_reason", ["completed", null, 1]);
    Exec(state, "DELETE FROM threads WHERE id=$p0", main);
    Capture(fixture, CodexCollector.Capture);
    Rows(db, "SELECT name,archived,is_pinned FROM current_threads WHERE git_sha IS NOT NULL", new object?[] { null, null, null });
    Count(db, "threads", 3);
    Count(db, "rollout_events", 23);
    Console.WriteLine("PASS: Codex rollout events, lineage, content, response usage and independent metadata changes");

    static string Stamp(string rows) => string.Join("\n", rows.Split('\n').Select((line, ordinal) =>
        "{\"timestamp\":\"2026-01-01T00:00:00Z\",\"ordinal\":" + ordinal + "," + line[1..])) + "\n";
}

async Task ClaudeChecks()
{
    using var fixture = new Fixture();
    var directory = Directory.CreateDirectory(Path.Combine(fixture.Root, "projects/test"));
    File.WriteAllText(Path.Combine(directory.FullName, "s.jsonl"), """
        {"type":"user","sessionId":"s","uuid":"u","parentUuid":null,"isSidechain":false,"cwd":"/work","gitBranch":"main","entrypoint":"cli","origin":{"kind":"human"},"promptSource":"repl","permissionMode":"default","message":{"role":"user","content":"Read the example."}}
        {"type":"custom-title","sessionId":"s","customTitle":"A"}
        {"type":"assistant","sessionId":"s","uuid":"thinking","parentUuid":"u","timestamp":"2026-01-01T00:00:00Z","message":{"id":"r","role":"assistant","model":"model","content":[{"type":"thinking","thinking":"private","signature":"example"}],"usage":{"input_tokens":10,"output_tokens":1}}}
        {"type":"assistant","sessionId":"s","uuid":"a","parentUuid":"thinking","timestamp":"2026-01-01T00:00:00Z","message":{"id":"r","role":"assistant","model":"model","content":[{"type":"text","text":"Checking."}],"usage":{"input_tokens":10,"output_tokens":2}}}
        {"type":"assistant","sessionId":"s","uuid":"call","parentUuid":"a","message":{"id":"r","role":"assistant","model":"model","content":[{"type":"tool_use","id":"tool-1","name":"Read","input":{"file_path":"example.txt","range":{"first":1}},"caller":{"type":"direct"}}],"usage":{"input_tokens":10,"output_tokens":2}}}
        {"type":"attachment","sessionId":"s","uuid":"attachment","parentUuid":"call","attachment":{"type":"hook_success","content":"private"}}
        {"type":"user","sessionId":"s","uuid":"result","parentUuid":"call","sourceToolAssistantUUID":"call","permissionDecision":{"decision":"allow","source":"rule","reasonType":"example"},"message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"tool-1","content":"success output"}]}}
        {"type":"assistant","sessionId":"s","uuid":"error-call","parentUuid":"result","message":{"id":"r2","role":"assistant","content":[{"type":"tool_use","id":"tool-2","name":"Read","input":{"file_path":"missing.txt"}}]}}
        {"type":"user","sessionId":"s","uuid":"error","parentUuid":"error-call","sourceToolAssistantUUID":"error-call","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"tool-2","is_error":true,"content":"Missing.\nTry another file."}]}}
        {"type":"assistant","sessionId":"s","uuid":"done","parentUuid":"error","message":{"id":"r3","role":"assistant","content":[{"type":"text","text":"Done."}]}}
        {"type":"system","subtype":"turn_duration","sessionId":"s","uuid":"system","parentUuid":"done","isMeta":true,"durationMs":100}
        {"type":"user","sessionId":"s","uuid":"slash","parentUuid":"system","message":{"role":"user","content":"<command-name>/status</command-name>"}}
        {"type":"user","sessionId":"s","uuid":"branch","parentUuid":"system","message":{"role":"user","content":[{"type":"image","source":{"type":"base64","media_type":"image/png","data":"AA=="},"extra":"omit"}]}}
        {"type":"assistant","sessionId":"s","uuid":"side","parentUuid":"slash","isSidechain":true,"agentId":"side-agent","message":{"role":"assistant","content":[{"type":"text","text":"Side task."}]}}
        {"type":"user","sessionId":"s","uuid":"meta","parentUuid":"branch","isMeta":true,"message":{"role":"user","content":"hidden"}}
        {"type":"future-event","sessionId":"s","uuid":"unknown","payload":"private"}
        {"type":"assistant","sessionId":"s","uuid":"future-block","parentUuid":"meta","message":{"role":"assistant","content":[{"type":"future","body":"private"}]}}
        {"type":"user","sessionId":"s","uuid":"document","parentUuid":"future-block","message":{"role":"user","content":[{"type":"document","title":"Example","source":{"type":"text","media_type":"text/plain","data":"sample"}}]}}
        {"type":"ai-title","sessionId":"s","aiTitle":"A"}
        {"type":"custom-title","sessionId":"s","customTitle":"A"}
        {"type":"ai-title","sessionId":"s","aiTitle":"Suggestion"}
        {"type":"ai-title","sessionId":"s","aiTitle":"Suggestion"}
        {"type":"custom-title","sessionId":"s","customTitle":"B"}
        {"type":"custom-title","sessionId":"s","customTitle":"A"}
        {"type":"file-history-snapshot","messageId":"u"}
        {"type":"api-request","sessionId":"s"}
        {"type":"queue-operation","sessionId":"s"}
        {"type":"agent-name","sessionId":"s"}
        {"type":"permission-mode","sessionId":"s"}
        """ + "\n");
    var subagents = Directory.CreateDirectory(Path.Combine(directory.FullName, "s", "subagents"));
    File.WriteAllText(Path.Combine(subagents.FullName, "agent-example.jsonl"), """
        {"type":"user","sessionId":"child","uuid":"child-root","parentUuid":null,"isSidechain":true,"agentId":"example","message":{"role":"user","content":"Child task."}}
        """ + "\n");

    Capture(fixture, ClaudeCollector.Capture);
    using var db = Open(fixture.Database("claude"));
    Capture(fixture, ClaudeCollector.Capture);
    Rows(db, "SELECT kind,title FROM session_title_observations ORDER BY observation_id",
        ["custom", "A"], ["ai", "A"], ["ai", "Suggestion"], ["custom", "B"], ["custom", "A"]);
    Rows(db, "SELECT session_id,parent_session_id,custom_title,ai_title FROM current_sessions ORDER BY session_id",
        ["child", "s", null, null], ["s", null, "A", "Suggestion"]);
    Rows(db, """
        SELECT uuid,parent_uuid,block_type,created_at_utc_ms,response_id,cwd,git_branch,entrypoint,prompt_source,permission_mode
        FROM transcript_events WHERE uuid IN ('u','thinking','slash','unknown') ORDER BY uuid
        """,
        ["slash", "system", "text", null, null, null, null, null, null, null],
        ["thinking", "u", "thinking", 1767225600000L, "r", null, null, null, null, null],
        ["u", "", "text", null, null, "/work", "main", "cli", "repl", "default"],
        ["unknown", null, null, null, null, null, null, null, null, null]);
    Rows(db, """
        SELECT uuid,parent_uuid,row_type,block_type,is_sidechain,agent_id
        FROM transcript_events WHERE uuid IN ('attachment','system','side','child-root') ORDER BY uuid
        """, ["attachment", "call", "attachment", "hook_success", 0, null], ["child-root", "", "user", "text", 1, "example"],
        ["side", "slash", "assistant", "text", 1, "side-agent"], ["system", "done", "system", "turn_duration", 0, null]);
    Rows(db, """
        SELECT uuid,visible_parent_uuid,text FROM message_timeline WHERE session_id='s' ORDER BY chain_order
        """, ["u", null, "Read the example."], ["a", "u", "Checking."], ["done", "a", "Done."],
        ["slash", "done", "<command-name>/status</command-name>"], ["side", "slash", "Side task."],
        ["branch", "done", null], ["document", "branch", null]);
    Rows(db, """
        SELECT uuid,json_extract(payload_json,'$.source.data'),json_type(payload_json,'$.extra'),json_extract(payload_json,'$.title')
        FROM transcript_events JOIN event_contents USING(observation_id) WHERE block_type IN ('image','document') ORDER BY uuid
        """, ["branch", "AA==", null, null], ["document", "sample", null, "Example"]);
    Rows(db, """
        SELECT call.uuid,result.uuid,json_extract(input.payload_json,'$.name'),json_extract(input.payload_json,'$.input.range.first'),
               json_type(output.payload_json,'$.is_error'),json_extract(output.payload_json,'$.content_length'),
               json_extract(output.payload_json,'$.permission_decision'),json_extract(output.payload_json,'$.permission_source'),
               json_type(output.payload_json,'$.content'),json_extract(output.payload_json,'$.error_text')
        FROM transcript_events call JOIN transcript_events result USING(session_id,tool_use_id)
        JOIN event_contents input ON input.observation_id=call.observation_id
        JOIN event_contents output ON output.observation_id=result.observation_id
        WHERE call.block_type='tool_use' AND result.block_type='tool_result' ORDER BY call.uuid
        """, ["call", "result", "Read", 1, "null", 14, "allow", "rule", null, null],
        ["error-call", "error", "Read", null, "true", 26, null, null, null, "Missing.\nTry another file."]);
    Rows(db, """
        SELECT e.uuid FROM transcript_events e LEFT JOIN event_contents c USING(observation_id)
        WHERE c.observation_id IS NULL ORDER BY e.uuid
        """, ["attachment"], ["future-block"], ["meta"], ["system"], ["thinking"], ["unknown"]);
    Rows(db, """
        SELECT COUNT(*) FROM transcript_events e WHERE e.parent_uuid != ''
          AND NOT EXISTS (SELECT 1 FROM transcript_events p WHERE (p.session_id,p.uuid)=(e.session_id,e.parent_uuid))
        """, [0]);
    Rows(db, "SELECT total_output_tokens FROM response_token_usage_observations ORDER BY observation_id", [1], [2]);
    Rows(db, "SELECT response_id,total_input_tokens,total_output_tokens FROM response_token_usage", ["r", 10, 2]);
    Count(db, "source_records", 25);
    Rows(db, "PRAGMA foreign_key_check");
    Rows(db, "SELECT stop_reason,error FROM capture_runs GROUP BY stop_reason", ["completed", null]);
    Console.WriteLine("PASS: Claude event chains, selected content, titles and response usage");
}

async Task ResidentRunChecks()
{
    using var fixture = new Fixture();
    var directory = Directory.CreateDirectory(Path.Combine(fixture.Root, "projects/test"));
    File.WriteAllText(Path.Combine(directory.FullName, "session.jsonl"), """
        {"type":"user","sessionId":"019b7a61-bc00-7111-8111-111111111111","uuid":"019b7a61-bc00-7111-8111-222222222222","parentUuid":null,"message":{"role":"user","content":"hi"}}
        """ + "\n");
    var arguments = new[] { "--store", fixture.Store, "capture" };
    for (var run = 1; run <= (OperatingSystem.IsWindows() ? 1 : 2); run++)
    {
        using var process = Start(RunProcess.Dotnet, [claude, .. arguments], fixture);
        process.StandardInput.Close();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var status = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (status?.StartsWith("Claude Code: ", StringComparison.Ordinal) != true)
                throw new Exception(status ?? await error.WaitAsync(timeout.Token));
            using var db = Open(fixture.Database("claude"));
            Rows(db, "SELECT run_id,confirmed_until_utc_ms IS NOT NULL FROM capture_runs WHERE run_id=(SELECT MAX(run_id) FROM capture_runs) AND stopped_at_utc_ms IS NULL", [run, 1]);
            Rows(db, "SELECT collector_version FROM capture_runs WHERE run_id=(SELECT MAX(run_id) FROM capture_runs)",
                [typeof(ClaudeCollector).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion]);
            if (run == 1) process.Kill();
            else if (!OperatingSystem.IsWindows()) process.Signal(PosixSignal.SIGTERM);
            await process.WaitForExitAsync(timeout.Token);
            // Windows signals a killed process before its file objects are cleaned up (seen 1.7 ms later in ETW),
            // so the next writer starts only once the killed writer's lock can be opened exclusively.
            if (run == 1) await Released(new Orisumi.Platform.Store(fixture.Store).LockFile("claude.writer"));
            if (run == 2) Check(process.ExitCode == 0, await error);
            Rows(db, "SELECT run_id,evidence,stopped_at_utc_ms IS NOT NULL,confirmed_until_utc_ms IS NOT NULL FROM capture_coverage WHERE scope='project_logs' ORDER BY run_id",
                run == 1 ? [[1, "unconfirmed", 0, 1]] : [[1, "unconfirmed", 0, 1], [2, "completed", 1, 1]]);
            Count(db, "source_records", 1);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
        }
    }
    if (OperatingSystem.IsWindows())
    {
        var previous = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", fixture.Root);
        try
        {
            using var stop = new CancellationTokenSource();
            var watch = ClaudeCollector.Watch(fixture.Store, stop.Token);
            stop.Cancel();
            await watch;
            using var db = Open(fixture.Database("claude"));
            Rows(db, "SELECT run_id,stop_reason,stopped_at_utc_ms IS NOT NULL FROM capture_runs ORDER BY run_id",
                [1, null, 0], [2, "completed", 1]);
        }
        finally { Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previous); }
    }
    Console.WriteLine("PASS: a killed run remains unconfirmed and graceful shutdown records completed");
}

static async Task Released(string path)
{
    var deadline = Stopwatch.StartNew();
    while (true)
    {
        try
        {
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return;
        }
        catch (IOException error) when ((error.HResult & 0xffff) == 32 && deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(10);
        }
    }
}

static Process Start(string executable, string[] arguments, Fixture? fixture = null)
{
    var start = new ProcessStartInfo(executable, arguments)
    {
        UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
    };
    if (fixture is not null)
    {
        // Point the source readers at fixtures in the child only; never import personal data.
        start.Environment["CODEX_HOME"] = fixture.Root;
        start.Environment["CLAUDE_CONFIG_DIR"] = fixture.Root;
    }
    return Process.Start(start) ?? throw new Exception($"Cannot start {executable}");
}

sealed class Fixture : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("orisumi-facts-");
    internal string Root => directory.FullName;
    internal string Store => Path.Combine(Root, "store");
    internal string Database(string source) => new Orisumi.Platform.Store(Store).CurrentDatabase(source)!;
    public void Dispose() => directory.Delete(recursive: true);
}
