#:project ../Collectors/Browser/Orisumi.Collectors.Browser.csproj
#:property AssemblyName=BrowserIntegrationChecks
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Orisumi.Collectors.Browser;
using Orisumi.Capture.Storage;
using Orisumi.Platform;

[assembly: SupportedOSPlatform("macos")]

if (args.Length > 1) throw new ArgumentException("Provide at most one Native AOT browser executable.");
if (args.Length == 1) Fixture.Executable = Path.GetFullPath(args[0]);

WriterFailure();
WriterConnection();
WriterCancellation();
await ExtensionVersion();
await UpgradePreviousBrowserSchema();
await RejectedDelivery();
await TabFocus();
await Tabs();
await BookmarksAndDownloads();
await History();
await Timeout();
await CorruptDatabase();
await WebSockets();
await Termination();
Console.WriteLine("Browser capture integration checks passed.");

static void WriterFailure()
{
    using var fixture = new Fixture();
    using var writer = new CaptureWriter(fixture.Store, BrowserDatabase.Collector);
    using var db = fixture.Database();
    Execute(db, """
        CREATE TRIGGER fail_diagnostic BEFORE INSERT ON capture_read_failures
        BEGIN SELECT RAISE(ABORT,'diagnostic write failed'); END
        """);
    try
    {
        writer.Run((command, _) => command.Execute("INSERT INTO missing_source VALUES(1)"));
        throw new Exception("The failed round returned success");
    }
    catch (IOException error) when (error.InnerException is SqliteException sqlite &&
        sqlite.Message.Contains("no such table: missing_source")) { }
    Equal(1L, writer.Run((command, _) => command.Scalar("SELECT 1")), "the next round can succeed");
    Console.WriteLine("PASS: a failed diagnostic write preserves the original SQLite error and allows another round");
}

static void WriterConnection()
{
    using var fixture = new Fixture();
    using var writer = new CaptureWriter(fixture.Store, BrowserDatabase.Collector);
    writer.Run((command, _) => command.Execute("CREATE TEMP TABLE resident_marker(id); INSERT INTO resident_marker VALUES(1)"));
    writer.Run((command, _) => command.Execute("CREATE TEMP TABLE round_marker(id)"), readOnlySources: []);
    var count = writer.Run((command, _) => command.Scalar("SELECT COUNT(*) FROM resident_marker"));
    Equal(1L, count, "ordinary rounds reuse the resident connection");
    Equal(0L, writer.Run((command, _) => command.Scalar("SELECT COUNT(*) FROM sqlite_temp_schema WHERE name='round_marker'")),
        "attachment rounds have their own connection");
    Console.WriteLine("PASS: ordinary rounds reuse the resident connection and attachment rounds are isolated");
}

static void WriterCancellation()
{
    using var fixture = new Fixture();
    using (var writer = new CaptureWriter(fixture.Store, BrowserDatabase.Collector))
    using (var stop = new CancellationTokenSource())
    {
        try
        {
            writer.Run<int>((command, run) =>
            {
                command.Execute("CREATE TABLE cancelled_work(value)");
                stop.Cancel();
                stop.Token.ThrowIfCancellationRequested();
                return 0;
            });
            throw new Exception("The cancelled round returned success");
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    using var db = fixture.Database();
    Equal(0L, Value(db, "SELECT COUNT(*) FROM sqlite_schema WHERE name='cancelled_work'"), "cancelled work rolls back");
    Equal(0L, Value(db, "SELECT COUNT(*) FROM capture_read_failures"), "shutdown is not a read failure");
    Equal("completed", Value(db, "SELECT stop_reason FROM capture_runs"), "shutdown closes the run");
    Console.WriteLine("PASS: cancellation rolls back the round and closes the run as completed without a read failure");
}

static async Task ExtensionVersion()
{
    using var fixture = new Fixture();
    var receiverVersion = BrowserDatabase.Collector.Version;
    const string oldVersion = "dev+old";
    foreach (var (version, reload) in new[] { (receiverVersion, false), (oldVersion, true), ((string?)null, false) })
    {
        await using var port = await fixture.Connect(profile: Guid.NewGuid(), extensionVersion: version);
        Equal(receiverVersion, port.OpenReply["receiverVersion"]?.GetValue<string>(), "open reports the receiver version");
        Equal(reload, port.OpenReply["reload"]?.GetValue<bool>(), "open requests reload only for a known mismatch");
    }
    using var db = fixture.Database();
    Equal(receiverVersion, Value(db, "SELECT extension_version FROM browser_runs WHERE run_id=1"), "matching version is recorded");
    Equal(oldVersion, Value(db, "SELECT extension_version FROM browser_runs WHERE run_id=2"), "mismatched version is recorded");
    Equal(null, Value(db, "SELECT extension_version FROM browser_runs WHERE run_id=3"), "unlabelled extension is recorded as unknown");
    Equal(1L, Value(db, "SELECT COUNT(*) FROM capture_coverage WHERE evidence='extension_version_mismatch'"),
        "only the mismatched run has a coverage gap");
    Equal(2L, Value(db, "SELECT run_id FROM capture_coverage WHERE evidence='extension_version_mismatch'"), "gap identifies the run");
    Equal($"extension={oldVersion}, receiver={receiverVersion}",
        Value(db, "SELECT detail FROM capture_coverage WHERE evidence='extension_version_mismatch'"), "gap explains both versions");
    Console.WriteLine("PASS: extension version negotiation, run identity and mismatch coverage");
}

static async Task UpgradePreviousBrowserSchema()
{
    using var fixture = new Fixture();
    using (var previous = CollectorDatabase.Open(fixture.Store, BrowserDatabase.Collector))
    {
        var coverage = (string)Value(previous, "SELECT sql FROM sqlite_schema WHERE name='capture_coverage'")!;
        var start = coverage.IndexOf("SELECT 'browser','notification'", StringComparison.Ordinal);
        var end = coverage.IndexOf("SELECT e.scope", start, StringComparison.Ordinal);
        Execute(previous, "DROP VIEW capture_coverage; ALTER TABLE browser_runs DROP COLUMN extension_version;");
        Execute(previous, coverage.Remove(start, end - start));
        Execute(previous, """
            INSERT INTO host_observations(observation_id,observed_at_utc_ms,platform,store_path) VALUES(1,1,'macos','test');
            INSERT INTO capture_runs(run_id,started_at_utc_ms,host_observation_id) VALUES(1,1,1);
            INSERT INTO browser_runs VALUES(1,'chrome','old-profile','old-session');
            """);
    }
    var version = BrowserDatabase.Collector.Version;
    await using (await fixture.Connect(extensionVersion: version)) { }
    using var db = fixture.Database();
    Equal("old-profile", Value(db, "SELECT profile_id FROM browser_runs WHERE run_id=1"), "previous browser run survives");
    Equal(null, Value(db, "SELECT extension_version FROM browser_runs WHERE run_id=1"), "previous run has unknown extension version");
    Equal(version, Value(db, "SELECT extension_version FROM browser_runs WHERE run_id=2"), "upgraded browser records the extension version");
    Console.WriteLine("PASS: previous browser schema accepts a new run with an extension version");
}

static async Task RejectedDelivery()
{
    using var fixture = new Fixture();
    var malformed = JsonNode.Parse("""{"tabId":4,"change":{"title":"discard me"}}""")!;
    await using (var port = await fixture.Connect())
    {
        var rejected = await port.Deliver(1, "tabs", "unknown_event", malformed);
        Equal(true, rejected["rejected"]?.GetValue<bool>(), "permanent failure acknowledges the queue head");
    }
    await using (var port = await fixture.Connect())
    {
        var replay = await port.Deliver(1, "tabs", "unknown_event", malformed, fragmented: true);
        Equal(true, replay["rejected"]?.GetValue<bool>(), "lost rejection ACK can be replayed");
        using var db = fixture.Database();
        using (var busy = db.BeginTransaction())
        {
            var retry = await port.Deliver(2, "tabs", "snapshot", JsonNode.Parse("""{"tabs":[],"windows":[]}""")!, expectedOk: false);
            Equal(null, retry["sequence"], "SQLite busy does not acknowledge the delivery");
            busy.Rollback();
        }
        await port.Deliver(2, "tabs", "snapshot", JsonNode.Parse("""{"tabs":[],"windows":[]}""")!);
        Equal(1L, Value(db, "SELECT COUNT(*) FROM browser_rejected_deliveries"), "rejection is idempotent across reconnects");
        Equal("discard me", Value(db, "SELECT json_extract(payload_json,'$.data.change.title') FROM browser_rejected_deliveries"),
            "original delivery remains available");
        Equal("invalid tab event", Value(db, "SELECT error FROM browser_rejected_deliveries"), "original error is recorded");
        Equal(1L, Value(db, "SELECT COUNT(*) FROM capture_coverage WHERE scope='tabs' AND evidence='delivery_rejected'"),
            "rejection is visible as a gap");
        Equal(2L, Value(db, "SELECT sequence FROM _browser_deliveries"), "valid delivery after rejection commits");
        Execute(db, """
            CREATE TRIGGER fail_reject BEFORE INSERT ON browser_rejected_deliveries
            BEGIN SELECT RAISE(ABORT,'fixture cannot persist the rejection'); END
            """);
        await port.Deliver(3, "tabs", "unknown_event", malformed, expectedOk: false);
        Equal(2L, Value(db, "SELECT sequence FROM _browser_deliveries"), "rejection is not confirmed until its evidence commits");
        Execute(db, "DROP TRIGGER fail_reject");

        var format = await port.Deliver(3, "tabs", "tab_updated", JsonNode.Parse("""{"tabId":4.5,"change":{}}""")!);
        Equal(true, format["rejected"]?.GetValue<bool>(), "invalid Chrome integer is permanently rejected");
        var json = await port.Exchange(new { type = "delivery", sequence = 4, scope = "tabs", browserSession = 123 });
        Equal(true, json["rejected"]?.GetValue<bool>(), "invalid envelope field type is permanently rejected");
        Equal(4L, json["sequence"]?.GetValue<long>(), "JSON deserialization failure acknowledges the exact sequence");
    }
    Console.WriteLine("PASS: permanent delivery rejection is durable, duplicate-safe and unblocks later deliveries; busy is retryable");
}

static async Task Tabs()
{
    using var fixture = new Fixture();
    var initial = JsonNode.Parse("""
        {"windows":[{"id":1,"focused":true},{"id":2,"focused":false}],
         "tabs":[{"id":4,"windowId":1,"active":true,"url":"https://example.test","title":"A"},
                 {"id":5,"windowId":2,"active":true,"url":"https://other.test","title":"Other"}],
         "focusedWindowId":1}
        """)!;
    var toB = JsonNode.Parse("""{"tabId":4,"change":{"title":"B"}}""")!;
    var toA = JsonNode.Parse("""{"tabId":4,"change":{"title":"A"}}""")!;

    await using (var port = await fixture.Connect())
    {
        await port.Deliver(1, "tabs", "snapshot", initial);
        await port.Deliver(2, "tabs", "tab_updated", toB);
    }
    using var db = fixture.Database();
    Equal("completed", Value(db, "SELECT stop_reason FROM capture_runs"), "WebSocket close confirms the stop");
    Equal(1L, Value(db, """
        SELECT COUNT(*) FROM capture_runs WHERE stopped_at_utc_ms >= confirmed_until_utc_ms
        AND confirmed_until_utc_ms >= started_at_utc_ms AND error IS NULL
        """), "run start, progress and end");
    await using (var port = await fixture.Connect())
    {
        await port.Deliver(2, "tabs", "tab_updated", toB);
        await port.Deliver(3, "tabs", "tab_updated", toA);
        await port.Deliver(2, "tabs", "tab_updated", toB);
        Equal("A,B,A", Value(db, """
            SELECT group_concat(title,',') FROM (SELECT title FROM tab_title_observations WHERE tab_key=
                (SELECT tab_key FROM tabs WHERE tab_id=4) ORDER BY observation_id)
            """), "transitions survive, even when a committed ACK was lost");

        await port.Deliver(4, "tabs", "snapshot", initial);
        await port.Deliver(5, "tabs", "snapshot", initial);
        await port.Deliver(6, "tabs", "tab_updated", toA);
        Equal(4L, Value(db, "SELECT COUNT(*) FROM tab_observations"), "new run baseline, then stationary snapshots and events do not write");
        Equal(0L, Value(db, "SELECT COUNT(*) FROM capture_channel_events"), "identical snapshot does not create a gap");

        await port.Deliver(7, "tabs", "window_focus_changed", JsonNode.Parse("""{"windowId":2}""")!);
        Equal(5L, Value(db, "SELECT tab_id FROM current_tabs WHERE is_focused=1"), "switching windows uses that window's last active tab");
        await port.Deliver(8, "tabs", "tab_detached", JsonNode.Parse("""{"tabId":4,"oldWindowId":1,"oldPosition":0}""")!);
        Equal(0L, Value(db, "SELECT has_window FROM current_tabs WHERE tab_id=4"), "detached tab has no window");
        await port.Deliver(9, "tabs", "tab_attached", JsonNode.Parse("""{"tabId":4,"newWindowId":1,"newPosition":0}""")!);
        await port.Deliver(10, "tabs", "tab_activated", JsonNode.Parse("""{"tabId":4,"windowId":1}""")!);
        var corrected = initial.DeepClone();
        corrected["tabs"]![0]!["title"] = "Missed title";
        corrected["focusedWindowId"] = 2;
        await port.Deliver(11, "tabs", "snapshot", corrected, fragmented: true);
        await port.Deliver(12, "tabs", "snapshot", corrected);
        Equal("Missed title", Value(db, "SELECT title FROM current_tabs WHERE tab_id=4"), "corrects the missed title event");
        Equal(1L, Value(db, "SELECT COUNT(*) FROM capture_channel_events WHERE evidence='reconciliation_changed'"), "one gap per inconsistent snapshot");
        Equal(2L, Value(db, """
            SELECT COUNT(*) FROM tab_history WHERE run_sequence=11 AND is_present=1 AND event_kind='snapshot'
            """), "reconciliation starts both tabs from the snapshot");
        Equal(1005L, Value(db, """
            SELECT valid_until_utc_ms FROM tab_history WHERE run_sequence=4 AND tab_id=4
            """), "the previous known interval ends at the last matching snapshot");
        Equal(0L, Value(db, "SELECT COUNT(*) FROM history_page_title_observations"), "tab updates do not write history titles");
        Equal(1L, Value(db, "SELECT COUNT(*) FROM capture_runs WHERE stopped_at_utc_ms IS NULL"), "one open run regardless of message count");
    }
    Equal(2L, Value(db, "SELECT COUNT(*) FROM capture_runs WHERE stop_reason='completed'"), "two connections produce two ended runs");
    Equal(1L, Value(db, "SELECT COUNT(*) FROM tabs WHERE tab_id=4"), "reconnect keeps the Chrome session identity");
    Equal(0L, Value(db, "SELECT COUNT(*) FROM pragma_foreign_key_check"), "all browser facts reference existing identities");
    Equal(0L, Value(db, "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='active_tab_focus_events'"),
        "foreground focus is derived from the two source facts");
    Console.WriteLine("PASS: tab A→B→A, stationary observations, replay across connections, focus, reconciliation and WebSocket close");
}

static async Task TabFocus()
{
    using var fixture = new Fixture();
    await using var port = await fixture.Connect();
    using var db = fixture.Database();
    var snapshot = JsonNode.Parse("""
        {"windows":[{"id":1},{"id":2}],"focusedWindowId":1,
         "tabs":[{"id":10,"windowId":1,"active":true},{"id":20,"windowId":2,"active":true},
                 {"id":21,"windowId":2,"active":false}]}
        """)!;
    await port.Deliver(1, "tabs", "snapshot", snapshot);
    snapshot["focusedWindowId"] = 2;
    snapshot["tabs"]![1]!["active"] = false;
    snapshot["tabs"]![2]!["active"] = true;
    await port.Deliver(2, "tabs", "snapshot", snapshot);
    Equal(21L, Value(db, "SELECT tab_id FROM current_tabs WHERE is_focused=1"), "focus and active tab both use the new snapshot");
    Equal(21L, Value(db, "SELECT tab_id FROM tab_history WHERE run_sequence=2 AND is_focused=1"),
        "history has one foreground tab after the combined observation");
    Equal(0L, Value(db, "SELECT is_focused FROM tab_history WHERE run_sequence=2 AND tab_id=20"),
        "history never foregrounds the previously active tab of the newly focused window");

    await port.Deliver(3, "tabs", "window_focus_changed", JsonNode.Parse("""{"windowId":3}""")!);
    Equal(3L, Value(db, "SELECT COUNT(*) FROM current_tabs WHERE is_focused IS NULL"), "focused window's active tab is unknown");
    Equal(3L, Value(db, "SELECT COUNT(*) FROM tab_history WHERE run_sequence=3 AND is_focused IS NULL"),
        "the loss of focus knowledge ends all previous focus states");
    snapshot["focusedWindowId"] = 3;
    snapshot["windows"]!.AsArray().Add(JsonNode.Parse("""{"id":3}"""));
    await port.Deliver(4, "tabs", "snapshot", snapshot);
    Equal(0L, Value(db, "SELECT has_tab FROM window_active_tab_events JOIN browser_windows USING(window_key) WHERE window_id=3"),
        "complete snapshot of a window without tabs records known empty");
    Equal(3L, Value(db, "SELECT COUNT(*) FROM current_tabs WHERE is_focused=0"), "no tab is focused when the focused window is empty");

    await port.Deliver(5, "tabs", "window_focus_changed", new JsonObject());
    Equal(null, Value(db, "SELECT has_window FROM browser_window_focus_events ORDER BY observation_id DESC LIMIT 1"),
        "a failed focus lookup records unknown");
    Equal(3L, Value(db, "SELECT COUNT(*) FROM current_tabs WHERE is_focused IS NULL"), "unknown window yields unknown foreground tab");
    await port.Deliver(6, "tabs", "window_focus_changed", JsonNode.Parse("""{"windowId":null}""")!);
    Equal(0L, Value(db, "SELECT has_window FROM browser_window_focus_events ORDER BY observation_id DESC LIMIT 1"),
        "an explicit no-window notification records known empty");
    Equal(3L, Value(db, "SELECT COUNT(*) FROM current_tabs WHERE is_focused=0"), "no focused window means no focused tab");
    Equal(3L, Value(db, "SELECT COUNT(*) FROM tab_history WHERE run_sequence=6 AND is_focused=0"),
        "the return of focus knowledge ends all previous unknown states");

    await port.Deliver(7, "tabs", "window_focus_changed", JsonNode.Parse("""{"windowId":2}""")!);
    await port.Deliver(8, "tabs", "tab_detached", JsonNode.Parse("""{"tabId":21,"oldWindowId":2}""")!);
    Equal(1L, Value(db, """
        SELECT COUNT(*) FROM window_active_tab_events JOIN tab_observations USING(observation_id)
        WHERE run_sequence=8 AND has_tab IS NULL AND tab_key IS NULL
        """), "detaching the active tab leaves its replacement unknown");
    Equal(3L, Value(db, "SELECT COUNT(*) FROM current_tabs WHERE is_focused IS NULL"), "active tab detachment clears stale focus");
    Console.WriteLine("PASS: foreground focus is derived at observation boundaries with distinct unknown and empty states");
}

static async Task BookmarksAndDownloads()
{
    using var fixture = new Fixture();
    await using var port = await fixture.Connect();
    using var db = fixture.Database();

    var tree = JsonNode.Parse("""
        [{"id":"0","title":""},{"id":"1","parentId":"0","title":"Bookmarks bar"},
         {"id":"2","parentId":"0","title":"Other bookmarks"},
         {"id":"3","parentId":"1","title":"A","url":"https://example.test","dateAdded":1770000000000}]
        """)!;
    await port.Deliver(1, "bookmarks", "snapshot", tree);
    await port.Deliver(2, "bookmarks", "changed", JsonNode.Parse("""{"id":"3","change":{"title":"B"}}""")!);
    await port.Deliver(3, "bookmarks", "changed", JsonNode.Parse("""{"id":"3","change":{"title":"A"}}""")!);
    await port.Deliver(4, "bookmarks", "moved", JsonNode.Parse("""{"id":"3","parentId":"1","index":2}""")!);
    await port.Deliver(5, "bookmarks", "moved", JsonNode.Parse("""{"id":"3","parentId":"2","index":0}""")!);
    tree[3]!["parentId"] = "2";
    await port.Deliver(6, "bookmarks", "snapshot", tree);
    Equal(4L, Value(db, "SELECT COUNT(*) FROM bookmark_observations"), "unchanged tree and same-parent move do not create facts");
    Equal("A,B,A", Value(db, """
        SELECT group_concat(title,',') FROM (SELECT title FROM bookmark_title_events f
        JOIN bookmarks USING(bookmark_key) WHERE bookmark_id='3' ORDER BY observation_id)
        """), "bookmark title A→B→A");
    tree[3]!["dateLastUsed"] = 1770000060000L;
    await port.Deliver(7, "bookmarks", "snapshot", tree);
    Equal(0L, Value(db, "SELECT COUNT(*) FROM capture_channel_events"), "last-use information is expected from polling");
    Equal("2", Value(db, "SELECT parent_id FROM current_bookmarks WHERE bookmark_id='3'"), "bookmark location stores only the parent");
    await port.Deliver(8, "bookmarks", "removed", JsonNode.Parse("""{"id":"2"}""")!);
    Equal(2L, Value(db, "SELECT COUNT(*) FROM current_bookmarks"), "folder removal applies to known descendants");

    var item = JsonNode.Parse("""
        {"id":12,"incognito":false,"url":"https://example.test/file","finalUrl":"https://example.test/file",
         "startTime":"2026-02-02T02:40:00.000Z","filename":"/tmp/A","mime":"text/plain","totalBytes":100,"state":"in_progress"}
        """)!;
    await port.Deliver(9, "downloads", "created", item);
    await port.Deliver(10, "downloads", "changed", JsonNode.Parse("""{"id":12,"filename":{"previous":"/tmp/A","current":"/tmp/B"}}""")!);
    await port.Deliver(11, "downloads", "changed", JsonNode.Parse("""{"id":12,"filename":{"previous":"/tmp/B","current":"/tmp/A"}}""")!);
    var done = JsonNode.Parse("""
        {"id":12,"state":{"previous":"in_progress","current":"complete"},
         "endTime":{"current":"2026-02-02T02:40:05.000Z"}}
        """)!;
    await port.Deliver(12, "downloads", "changed", done);
    await port.Deliver(13, "downloads", "changed", done);
    Equal("complete:/tmp/A:100", Value(db, "SELECT state||':'||local_path||':'||total_bytes FROM current_downloads"), "delta keeps unrelated fields");
    Equal(3L, Value(db, "SELECT COUNT(*) FROM download_path_observations"), "download path A→B→A");
    Equal(2L, Value(db, "SELECT COUNT(*) FROM download_state_observations"), "state only changes on completion");
    Equal(1L, Value(db, "SELECT COUNT(*) FROM download_response_observations"), "response fields stay independent");
    var full = new JsonArray(item.DeepClone());
    full[0]!["state"] = "complete";
    full[0]!["endTime"] = "2026-02-02T02:40:05.000Z";
    await port.Deliver(14, "downloads", "snapshot", full);
    await port.Deliver(15, "downloads", "snapshot", full);
    Equal(5L, Value(db, "SELECT COUNT(*) FROM download_observations"), "steady full download list does not duplicate observations");
    await port.Deliver(16, "downloads", "snapshot", new JsonArray());
    await port.Deliver(17, "downloads", "snapshot", new JsonArray());
    Equal(1L, Value(db, "SELECT COUNT(*) FROM capture_channel_events WHERE scope='downloads' AND evidence='reconciliation_changed'"),
        "a missed removal is reconciled once");
    Equal("complete:/tmp/A", Value(db, "SELECT state||':'||local_path FROM current_downloads"), "erasure preserves the last download facts");
    Console.WriteLine("PASS: bookmark parent-only changes, download field groups and full-list reconciliation");
}

static async Task History()
{
    using var fixture = new Fixture();
    await using var port = await fixture.Connect();
    using var db = fixture.Database();
    var next = (await port.Exchange(new { type = "history_poll" }))["history"]!;
    Equal("backfill", next["direction"]!.GetValue<string>(), "receiver chooses the history range");
    var previous = next["cursor"]!.GetValue<long>();
    var range = next.DeepClone();
    range["startTime"] = previous - 600_000;
    range["pages"] = JsonNode.Parse("""
        [{"url":"https://example.test","title":"Chrome history title","visits":[
            {"id":"15","visitId":"155","visitTime":1770000000000.5,"transition":"link","referringVisitId":"0","isLocal":true},
            {"id":"15","visitId":"156","visitTime":1770000001000,"transition":"reload","referringVisitId":"155","isLocal":true}
        ]}]
        """);
    Execute(db, """
        CREATE TRIGGER fail_second_visit BEFORE INSERT ON visits WHEN NEW.visit_id='156'
        BEGIN SELECT RAISE(ABORT,'fixture rejects the second visit'); END;
        """);
    await port.Deliver(1, "history", "range", range, expectedOk: false);
    Equal(0L, Value(db, "SELECT COUNT(*) FROM visits"), "failed range rolls back earlier visits");
    Equal(0L, Value(db, "SELECT COUNT(*) FROM history_pages"), "failed range rolls back its page title");
    Equal(previous, Value(db, "SELECT backfill_before_utc_ms FROM _history_cursors"), "failed visits do not advance the cursor");
    Equal(0L, Value(db, "SELECT COUNT(*) FROM _browser_deliveries"), "failed range is not acknowledged");
    Equal(1L, Value(db, "SELECT COUNT(*) FROM capture_read_failures"), "failed round records one read failure");
    Equal(null, Value(db, "SELECT stopped_at_utc_ms FROM capture_runs"), "failed round keeps the run open");

    Execute(db, "DROP TRIGGER fail_second_visit");
    var reply = await port.Deliver(1, "history", "range", range);
    Equal(previous - 600_000, reply["history"]!["cursor"]!.GetValue<long>(), "host asks for the next range after commit");
    Equal(previous - 600_000, Value(db, "SELECT backfill_before_utc_ms FROM _history_cursors"), "cursor commits with successful visits");
    Equal(2L, Value(db, "SELECT COUNT(*) FROM visits"), "retry commits both visits");
    Equal("", Value(db, "SELECT referring_visit_id FROM visits WHERE visit_id='155'"), "Chrome no-referrer sentinel");
    Equal("Chrome history title", Value(db, "SELECT title FROM history_pages"), "history ranges still capture page titles");
    await port.Deliver(1, "history", "range", range);
    Equal(2L, Value(db, "SELECT COUNT(*) FROM visits"), "repeat range does not duplicate visits");
    Equal(1L, Value(db, "SELECT COUNT(*) FROM history_page_title_observations"), "repeat range does not duplicate titles");
    Equal(1L, Value(db, "SELECT COUNT(*) FROM capture_coverage WHERE evidence='range_read'"), "history coverage comes from the committed cursor");
    Console.WriteLine("PASS: history receiver range, retained visits and titles, atomic failure and retry");
}

static async Task Timeout()
{
    using var fixture = new Fixture();
    await using var port = await fixture.Connect(TimeSpan.FromMilliseconds(250));
    using var db = fixture.Database();
    var empty = JsonNode.Parse("""{"windows":[],"tabs":[],"focusedWindowId":null}""")!;
    var observedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    await port.Deliver(1, "tabs", "snapshot", empty, observedAt: observedAt);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (!Equals(1L, Value(db, "SELECT COUNT(*) FROM capture_channel_events WHERE evidence='snapshot_timeout'")))
        await Task.Delay(10, deadline.Token);
    Equal(null, Value(db, "SELECT stopped_at_utc_ms FROM capture_channel_events"), "timeout opens a gap while the pipe is still open");
    await port.Deliver(2, "bookmarks", "snapshot", new JsonArray());
    await Task.Delay(300, deadline.Token);
    Equal(1L, Value(db, "SELECT COUNT(*) FROM capture_channel_events"), "continued silence records one gap even with other messages");
    await port.Deliver(3, "tabs", "snapshot", empty, observedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    Equal(1L, Value(db, "SELECT COUNT(*) FROM capture_channel_events WHERE stopped_at_utc_ms > started_at_utc_ms"), "fresh snapshot closes the gap");
    Equal(2L, Value(db, "SELECT COUNT(*) FROM tab_observations"), "recovery reestablishes the initial state");
    Console.WriteLine("PASS: snapshot deadline, one gap during silence, and recovery on the same connection");
}

static async Task CorruptDatabase()
{
    using var fixture = new Fixture();
    var path = new Store(fixture.Store).WritableDatabase("browser").Path;
    File.WriteAllText(path, "invalid database contents");
    await using var port = await fixture.Connect(expectedOk: false);
    Equal("invalid database contents", File.ReadAllText(path), "host does not modify a corrupt database");
    Console.WriteLine("PASS: opening an invalid database returns a failure without modifying it");
}

static async Task WebSockets()
{
    using var fixture = new Fixture();
    var endpoint = fixture.Start(TimeSpan.FromMinutes(2), TimeSpan.FromMilliseconds(100));
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    foreach (var origin in new[] { null, "chrome-extension://wrong" })
    {
        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        if (origin is not null) socket.Options.SetRequestHeader("Origin", origin);
        try { await socket.ConnectAsync(endpoint, deadline.Token); throw new Exception("unexpected WebSocket upgrade"); }
        catch (WebSocketException) when (socket.HttpStatusCode == HttpStatusCode.Forbidden) { }
    }
    await using var first = await fixture.Connect();
    await using (var duplicate = await fixture.Connect(expectedOk: false))
    {
        Equal(WebSocketMessageType.Close, (await duplicate.Receive(deadline.Token)).Type, "duplicate profile gets an error and then closes");
    }
    await using (var second = await fixture.Connect(profile: Guid.NewGuid()))
    {
        var snapshot = JsonNode.Parse("""{"tabs":[],"windows":[]}""")!;
        var large = snapshot.DeepClone();
        large["padding"] = new string('x', 2 * 1024 * 1024);
        await Task.WhenAll(first.Deliver(1, "tabs", "snapshot", large, fragmented: true),
            second.Deliver(1, "tabs", "snapshot", snapshot.DeepClone()));
        var history = await second.Exchange(new { type = "history_poll" });
        Equal(true, history["history"] is not null, "history poll returns a range");
        await first.Heartbeat(deadline.Token);
        await second.Heartbeat(deadline.Token);
        using var database = fixture.Database();
        Equal(2L, Value(database, "SELECT COUNT(*) FROM capture_runs WHERE stop_reason IS NULL"), "two profile runs are active concurrently");
    }
    using var remaining = fixture.Database();
    Equal(1L, Value(remaining, "SELECT COUNT(*) FROM capture_runs WHERE stop_reason='completed'"), "closing one socket completes only its run");
    Console.WriteLine("PASS: loopback WebSocket Origin rejects missing and wrong origins; concurrent profiles, duplicate lock, fragmented delivery, history and heartbeats");
}

static async Task Termination()
{
    using var fixture = new Fixture(external: true);
    await using var first = await fixture.Connect();
    await using var second = await fixture.Connect(profile: Guid.NewGuid());
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    using (var conflict = fixture.StartProcess())
    {
        try
        {
            var diagnostics = conflict.StandardError.ReadToEndAsync(deadline.Token);
            await conflict.WaitForExitAsync(deadline.Token);
            Equal(1, conflict.ExitCode, "an occupied port exits nonzero");
            var message = await diagnostics;
            Equal(true, message.Contains($"port {fixture.Instance.BrowserPort}") && message.Contains(fixture.Instance.Name),
                "bind failure names the instance and port");
        }
        finally
        {
            if (!conflict.HasExited) { conflict.Kill(); await conflict.WaitForExitAsync(); }
        }
    }
    if (Fixture.Executable is not null)
    {
        using var heartbeat = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await Task.WhenAll(first.Heartbeat(heartbeat.Token), second.Heartbeat(heartbeat.Token));
        Console.WriteLine("PASS: Native AOT WebSockets send the production 20-second heartbeat");
    }
    using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    var elapsed = Stopwatch.StartNew();
    fixture.Process!.Signal(PosixSignal.SIGTERM);
    await Task.WhenAll(first.ExpectClose(shutdown.Token), second.ExpectClose(shutdown.Token));
    await fixture.Process.WaitForExitAsync(shutdown.Token);
    Equal(0, fixture.Process.ExitCode, "SIGTERM exits successfully");
    using var database = fixture.Database();
    Equal(2L, Value(database, "SELECT COUNT(*) FROM capture_runs WHERE stop_reason='completed' AND error IS NULL"), "SIGTERM completes all runs");
    Console.WriteLine($"PASS: bind diagnostics and SIGTERM closed both sockets and completed both runs in {elapsed.ElapsedMilliseconds} ms");
}

static object? Value(SqliteConnection db, string sql)
{
    using var statement = db.CreateCommand();
    statement.CommandText = sql;
    var value = statement.ExecuteScalar();
    return value is DBNull ? null : value;
}
static void Execute(SqliteConnection db, string sql)
{
    using var statement = db.CreateCommand();
    statement.CommandText = sql;
    statement.ExecuteNonQuery();
}
static void Equal(object? expected, object? actual, string reason)
{
    if (!Equals(expected, actual)) throw new Exception($"{reason}: expected {expected ?? "NULL"}, got {actual ?? "NULL"}");
}

sealed class Fixture(bool external = false) : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("orisumi-browser-");
    private readonly Guid profileId = Guid.NewGuid();
    private readonly Guid queueId = Guid.NewGuid();
    private readonly Guid browserSession = Guid.NewGuid();
    private readonly CancellationTokenSource stop = new();
    private Task? server;
    private Uri? endpoint;
    private Task<string>? diagnostics;
    internal static string? Executable { get; set; }
    internal Instance Instance { get; private set; } = null!;
    internal Process? Process { get; private set; }
    internal string Store => directory.FullName;

    internal Uri Start(TimeSpan? interval = null, TimeSpan? heartbeat = null)
    {
        if (endpoint is not null) return endpoint;
        var name = "test_browser";
        if (external)
        {
            // The CLI uses its instance's fixed port. Find an unused test name for that port.
            var probes = Directory.CreateDirectory(Path.Combine(Store, "probes"));
            do
            {
                name = "test_browser_" + Guid.NewGuid().ToString("N");
                var probe = Instance.Create(Path.Combine(probes.FullName, name), name);
                if (IsFree(probe.BrowserPort)) break;
            } while (true);
        }
        Instance = Instance.Create(Store, name);
        var port = external ? Instance.BrowserPort : FreePort();
        endpoint = new Uri($"ws://127.0.0.1:{port}/");
        if (external)
        {
            Process = StartProcess();
            diagnostics = Process.StandardError.ReadToEndAsync();
        }
        else
            server = BrowserServer.Run(Instance, port, interval ?? TimeSpan.FromMinutes(2),
                heartbeat ?? TimeSpan.FromSeconds(20), stop.Token);
        return endpoint;
    }

    internal Process StartProcess()
    {
        var managed = Path.GetFullPath("Collectors/Browser/bin/Debug/net11.0/orisumi-browser.dll");
        var start = new ProcessStartInfo(Executable ?? RunProcess.Dotnet)
        {
            UseShellExecute = false, RedirectStandardError = true,
        };
        if (Executable is null) start.ArgumentList.Add(managed);
        foreach (var argument in new[] { "--store", Store, "capture" }) start.ArgumentList.Add(argument);
        return System.Diagnostics.Process.Start(start)!;
    }

    internal async Task<Connection> Connect(TimeSpan? interval = null, bool expectedOk = true, Guid? profile = null,
        string? extensionVersion = null)
    {
        var address = Start(interval);
        if (server?.IsCompleted == true) await server;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (external)
            while (true)
            {
                if (Process!.HasExited) throw new IOException(await diagnostics!);
                using var tcp = new TcpClient();
                try { await tcp.ConnectAsync(IPAddress.Loopback, address.Port, deadline.Token); break; }
                catch (SocketException) { await Task.Delay(20, deadline.Token); }
            }

        var socket = new ClientWebSocket();
        var origin = "chrome-extension://" + ChromeExtension.Id(ChromeExtension.DirectoryFor(Instance));
        socket.Options.SetRequestHeader("Origin", origin);
        try { await socket.ConnectAsync(address, deadline.Token); }
        catch { socket.Dispose(); throw; }
        var session = profile.HasValue ? Guid.NewGuid() : browserSession;
        var queue = profile.HasValue ? Guid.NewGuid() : queueId;
        var connection = new Connection(socket, session);
        try
        {
            connection.OpenReply = await connection.Exchange(new
            {
                type = "open", profileId = profile ?? profileId, queueId = queue, browserSession = session, extensionVersion,
            }, expectedOk);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    internal SqliteConnection Database()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = new Store(Store).CurrentDatabase("browser"), Pooling = false, DefaultTimeout = 3,
        }.ToString());
        db.Open();
        return db;
    }

    public void Dispose()
    {
        try
        {
            stop.Cancel();
            server?.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (Process is { HasExited: false })
            {
                Process.Signal(PosixSignal.SIGTERM);
                if (!Process.WaitForExit(5000)) { Process.Kill(); Process.WaitForExit(); }
            }
            if (diagnostics?.GetAwaiter().GetResult() is { Length: > 0 } error) Console.Error.Write(error);
        }
        finally
        {
            Process?.Dispose();
            stop.Dispose();
            directory.Delete(recursive: true);
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static bool IsFree(int port)
    {
        using var listener = new TcpListener(IPAddress.Loopback, port);
        try { listener.Start(); return true; }
        catch (SocketException) { return false; }
    }
}

sealed class Connection(ClientWebSocket socket, Guid browserSession) : IAsyncDisposable
{
    internal JsonNode OpenReply { get; set; } = null!;

    internal async Task<JsonNode> Deliver(long sequence, string scope, string kind, JsonNode data, bool expectedOk = true,
        bool fragmented = false, long? observedAt = null)
    {
        var payload = new { type = "delivery", sequence, scope, kind, data, browserSession, observedAtUtcMs = observedAt ?? 1000 + sequence };
        var reply = await Exchange(payload, expectedOk, fragmented);
        if (expectedOk && reply["sequence"]?.GetValue<long>() != sequence)
            throw new Exception("The collector must acknowledge the exact sequence");
        return reply;
    }

    internal async Task<JsonNode> Exchange(object value, bool expectedOk = true, bool fragmented = false)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cancellation.Token;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        var offset = 0;
        if (fragmented)
        {
            offset = bytes.Length / 2;
            await socket.SendAsync(bytes.AsMemory(0, offset), WebSocketMessageType.Text, false, token);
        }
        await socket.SendAsync(bytes.AsMemory(offset), WebSocketMessageType.Text, true, token);
        while (true)
        {
            var (type, reply) = await Receive(token);
            if (reply?["type"]?.GetValue<string>() == "heartbeat") continue;
            if (type != WebSocketMessageType.Text || reply?["ok"]?.GetValue<bool>() != expectedOk)
                throw new Exception($"Unexpected WebSocket acknowledgement: {type} {reply}");
            return reply!;
        }
    }

    internal async Task<(WebSocketMessageType Type, JsonNode? Message)> Receive(CancellationToken token)
    {
        var buffer = new byte[4096];
        using var stream = new MemoryStream();
        while (true)
        {
            var frame = await socket.ReceiveAsync(buffer.AsMemory(), token);
            if (frame.MessageType == WebSocketMessageType.Close) return (frame.MessageType, null);
            stream.Write(buffer.AsSpan(0, frame.Count));
            if (frame.EndOfMessage) return (frame.MessageType, JsonNode.Parse(stream.ToArray()));
        }
    }

    internal async Task Heartbeat(CancellationToken token)
    {
        var (type, reply) = await Receive(token);
        if (type != WebSocketMessageType.Text || reply?["type"]?.GetValue<string>() != "heartbeat")
            throw new Exception($"Expected heartbeat, got {type} {reply}");
    }

    internal async Task ExpectClose(CancellationToken token)
    {
        while (true)
        {
            var (type, reply) = await Receive(token);
            if (type == WebSocketMessageType.Close && socket.CloseStatus == WebSocketCloseStatus.NormalClosure) return;
            if (reply?["type"]?.GetValue<string>() != "heartbeat") throw new Exception($"Expected close frame, got {type} {reply}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        using (socket)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (socket.State == WebSocketState.Open)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancellation.Token);
        }
    }
}
