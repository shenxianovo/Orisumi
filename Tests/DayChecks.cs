#:project ../Host/Cli/Orisumi.Cli.csproj
#:property AssemblyName=DayChecks
#:property PublishAot=false

using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orisumi.Cli;
using Orisumi.Cli.Day;
using Orisumi.Platform;

[assembly: SupportedOSPlatform("macos")]

var day = DayRange.Create("2026-11-01", "America/New_York");
Equal(25 * 3_600_000L, day.To - day.From, "fall-back date is 25 hours");
Equal("2026-11-01T00:00:00-04:00", day.Format(day.From), "offset before fall-back");
Equal("2026-11-02T00:00:00-05:00", day.Format(day.To), "offset after fall-back");
var spring = DayRange.Create("2026-03-08", "America/New_York");
Equal(23 * 3_600_000L, spring.To - spring.From, "spring-forward date is 23 hours");

var today = DayRange.Create(null, "UTC");
Check(today.To <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() && today.To > today.From, "today ends now, not at midnight");
var terminal = new DayApp("Terminal", "com.apple.Terminal");
var titleTotals = DayDevice.TitleTotals([
    new(0, 40_000, new(terminal, "a", null), 40_000),
    new(40_000, 41_000, new(terminal, null, null), 0),
    new(41_000, 71_000, new(terminal, "a", null), 10_000),
    new(71_000, 80_000, new(terminal, "b", null), 9_000),
], 60_000).ToArray();
Check(titleTotals is [{ Title: "a", Foreground: 70_000, Active: 50_000 }], "title totals add separate segments and drop short titles");

var skippedMidnight = DayRange.Create("2026-09-06", "America/Santiago");
Equal("2026-09-06T01:00:00-03:00", skippedMidnight.Format(skippedMidnight.From), "skipped midnight starts at first valid local time");

var runs = new Dictionary<long, DaySpan<long>>
{
    [1] = new(day.From - 40, day.From + 100, 1),
    [2] = new(day.To - 100, day.To + 40, 2),
};
var spans = DayTimeline.Values(day, runs, new DayChange<string>[]
{
    new(new(1, 2, day.From + 20), "next"),
    new(new(1, 1, day.From - 20), "initial"),
    new(new(2, 1, day.To - 200), "late"),
    new(new(3, 1, day.From), "no run"),
}).ToArray();
Check(spans.SequenceEqual([
    new(day.From, day.From + 20, "initial"),
    new(day.From + 20, day.From + 100, "next"),
    new(day.To - 100, day.To, "late"),
]), "intervals follow run sequence and clip to both day and run");
var interrupted = DayTimeline.Values(day, runs, new DayChange<string>[]
{
    new(new(1, 1, day.From - 20), "initial"),
    new(new(1, 3, day.From + 40), "during interruption"),
    new(new(1, 5, day.From + 80), "fresh initial"),
}, position => position.Sequence switch { 1 => day.From + 30, 3 => null, _ => long.MaxValue });
Check(interrupted.SequenceEqual([
    new(day.From, day.From + 30, "initial"),
    new(day.From + 80, day.From + 100, "fresh initial"),
]), "channel cutoff ends a value and requires a fresh initial row");

var temp = Directory.CreateTempSubdirectory("orisumi-day-");
try
{
    var store = new Store(temp.FullName);
    var hardware = HostIdentity.Read().HardwareUuid ?? "local-device";
    var from = day.From;
    var to = day.To;
    Fixture("macos", "primary", hardware, $"""
        CREATE TABLE capture_runs(run_id, started_at_utc_ms, stopped_at_utc_ms, confirmed_until_utc_ms);
        INSERT INTO capture_runs VALUES (1,{from - 500},{from + 700},{from + 920}), (2,{from + 800},NULL,{from + 900}),
            (3,{from + 920},NULL,NULL);
        CREATE TABLE capture_channel_events(run_id, run_sequence, observed_at_utc_ms, channel, available);
        INSERT INTO capture_channel_events VALUES (1,12,{from + 200},'afk',0), (1,13,{from + 240},'afk',1),
            (1,9,{from + 90},'windows',0), (1,18,{from + 300},'windows',1),
            (1,23,{from + 550},'windows',0), (1,24,{from + 580},'windows',1),
            (1,20,{from + 370},'window_title',0), (1,21,{from + 390},'window_title',1),
            (1,31,{from + 635},'applications',0), (1,32,{from + 650},'applications',1);
        CREATE TABLE system_sleep_events(run_id, run_sequence, observed_at_utc_ms, event_kind);
        INSERT INTO system_sleep_events VALUES (1,3,{from + 120},'sleep'), (1,4,{from + 180},'wake');
        CREATE TABLE user_session_events(run_id, run_sequence, observed_at_utc_ms, event_kind);
        INSERT INTO user_session_events VALUES (1,7,{from + 150},'session_inactive'), (1,8,{from + 185},'session_active');
        CREATE TABLE screen_lock_events(run_id, run_sequence, observed_at_utc_ms, is_locked);
        INSERT INTO screen_lock_events VALUES (1,3,{from + 100},1), (1,4,{from + 190},NULL);
        CREATE TABLE afk_events(run_id, run_sequence, observed_at_utc_ms, state);
        INSERT INTO afk_events VALUES (1,2,{from - 200},'active'), (1,14,{from + 260},'active'),
            (1,15,{from + 340},'idle'), (1,16,{from + 370},'idle'), (1,17,{from + 400},'active'),
            (2,1,{from + 800},'active'), (3,1,{from + 920},'active');
        CREATE TABLE time_zone_events(run_id, run_sequence, observed_at_utc_ms, time_zone_id);
        INSERT INTO time_zone_events VALUES (1,1,{from - 400},'Asia/Shanghai'), (1,31,{from + 660},'Asia/Tokyo'),
            (2,2,{from + 800},'Asia/Tokyo'), (3,2,{from + 920},'UTC');
        CREATE TABLE process_instances(process_instance_id, bundle_id, app_name);
        INSERT INTO process_instances VALUES ('terminal','com.apple.Terminal','Terminal'), ('chrome','com.google.Chrome','Chrome');
        CREATE TABLE foreground_app_events(run_id, run_sequence, observed_at_utc_ms, has_app, process_instance_id);
        INSERT INTO foreground_app_events VALUES (1,2,{from - 150},1,'terminal'), (1,17,{from + 300},1,'chrome'),
            (1,25,{from + 600},1,'terminal'), (1,33,{from + 660},1,'terminal'), (1,34,{from + 680},0,NULL);
        CREATE TABLE focused_window_events(run_id, run_sequence, observed_at_utc_ms, has_window, focus_id);
        INSERT INTO focused_window_events VALUES (1,3,{from - 140},1,'first'), (1,10,{from + 90},NULL,NULL),
            (1,19,{from + 300},1,'second'), (1,22,{from + 500},0,NULL),
            (1,27,{from + 620},0,NULL);
        CREATE TABLE window_focus_instances(focus_id, process_instance_id);
        INSERT INTO window_focus_instances VALUES ('first','terminal'),('second','chrome');
        CREATE TABLE window_title_observations(run_id, run_sequence, observed_at_utc_ms, focus_id, title);
        INSERT INTO window_title_observations VALUES (1,3,{from - 140},'first','Task (1)'),(1,5,{from + 70},'first','Task (2)'),
            (1,19,{from + 300},'second','Browser'), (1,30,{from + 410},'second','Browser');
        """);
    Fixture("macos", "replica", hardware, $"""
        CREATE TABLE capture_runs(run_id, started_at_utc_ms, stopped_at_utc_ms);
        INSERT INTO capture_runs VALUES (1,{from + 950},{from + 1050});
        CREATE TABLE capture_channel_events(run_id, run_sequence, observed_at_utc_ms, channel, available);
        CREATE TABLE afk_events(run_id, run_sequence, observed_at_utc_ms, state);
        INSERT INTO afk_events VALUES (1,1,{from + 950},'idle');
        CREATE TABLE screen_lock_events(run_id,run_sequence,observed_at_utc_ms);
        """, nameAt: 2, name: "Newest name");
    Fixture("macos", "remote", "different-device", $"""
        CREATE TABLE capture_runs(run_id, started_at_utc_ms, stopped_at_utc_ms, confirmed_until_utc_ms);
        INSERT INTO capture_runs VALUES (1,{to - 100},{to + 30},NULL);
        CREATE TABLE capture_channel_events(run_id, run_sequence, observed_at_utc_ms, channel, available);
        CREATE TABLE afk_events(run_id, run_sequence, observed_at_utc_ms, state);
        INSERT INTO afk_events VALUES (1,1,{to - 100},'active');
        """);
    Fixture("macos", "unidentified", null, "CREATE TABLE host_observations(observed_at_utc_ms);");
    Fixture("unknown", "null-hardware", null, """
        CREATE TABLE database_identity(platform, hardware_uuid);
        INSERT INTO database_identity VALUES ('macos',NULL);
        """);

    Fixture("browser", "browser", hardware, $"""
        CREATE TABLE capture_runs(run_id, started_at_utc_ms, stopped_at_utc_ms, confirmed_until_utc_ms);
        INSERT INTO capture_runs VALUES (1,{from + 10},NULL,{from + 560}), (2,{from + 10},{from + 580},NULL),
            (3,{from + 10},{from + 580},NULL);
        CREATE TABLE capture_channel_events(run_id,scope,started_at_utc_ms,stopped_at_utc_ms,evidence);
        INSERT INTO capture_channel_events VALUES (1,'tabs',{from + 470},{from + 490},'snapshot_timeout');
        CREATE TABLE browser_runs(run_id,profile_id,browser_session);
        INSERT INTO browser_runs VALUES (1,'one','chrome'),(2,'two','chrome'),(3,'one','chrome');
        CREATE TABLE tab_observations(observation_id,run_id,run_sequence,observed_at_utc_ms,browser_session);
        INSERT INTO tab_observations VALUES (1,1,1,{from + 20},'chrome'),(2,2,1,{from + 35},'chrome'),
            (3,1,2,{from + 320},'chrome'),(4,1,3,{from + 450},'chrome'),(5,1,4,{from + 480},'chrome'),
            (6,1,5,{from + 495},'chrome'),(7,1,6,{from + 510},'chrome'),(8,3,1,{from + 565},'chrome');
        CREATE TABLE browser_window_focus_events(observation_id,has_window,window_key);
        INSERT INTO browser_window_focus_events VALUES (1,1,10),(2,0,NULL),(4,1,20),(7,1,20);
        CREATE TABLE window_active_tab_events(observation_id,window_key,has_tab,tab_key);
        INSERT INTO window_active_tab_events VALUES (1,10,1,100),(2,30,1,300),(4,20,1,200),(7,20,1,200);
        CREATE TABLE tab_url_observations(observation_id,tab_key,url);
        INSERT INTO tab_url_observations VALUES (1,100,'https://first.test'),(2,300,'https://background.test'),(4,200,'https://second.test'),
            (5,200,'https://gap.test'),(7,200,'https://second.test');
        CREATE TABLE tab_title_observations(observation_id,tab_key,title);
        INSERT INTO tab_title_observations VALUES (1,100,'Page 1'),(3,100,'Page 2'),(4,200,'Next 3'),(7,200,'Next 4');
        CREATE TABLE visits(browser,profile_id,visited_at_utc_ms,url,transition,is_local);
        INSERT INTO visits VALUES ('chrome','one',{from + 40},'https://first.test','typed',1),
            ('chrome','one',{from + 30},'https://first.test','link',0), ('chrome','one',{to},'https://first.test','typed',1);
        CREATE TABLE history_page_title_observations(observation_id,browser,profile_id,url,title,observed_at_utc_ms);
        INSERT INTO history_page_title_observations VALUES (1,'chrome','one','https://first.test','Latest',20),
            (2,'chrome','two','https://first.test','Wrong profile',25);
        CREATE TABLE downloads(download_key,started_at_utc_ms,url);
        INSERT INTO downloads VALUES (1,{from},'https://first.test/initial'), (2,{to},'https://first.test/next-day');
        CREATE TABLE download_latest(download_key,final_url,local_path,state);
        INSERT INTO download_latest VALUES (1,'https://first.test/final','/tmp/file','complete');
        CREATE VIEW current_downloads AS SELECT * FROM download_latest;
        """ + File.ReadAllText("Collectors/Browser/Schema/Views/active_tab_focus.sql") +
        File.ReadAllText("Collectors/Browser/Schema/Views/history_pages.sql"));
    Fixture("browser", "remote-browser", "different-device", """
        CREATE TABLE visits(browser,profile_id,visited_at_utc_ms,url,transition,is_local);
        CREATE VIEW history_pages AS SELECT * FROM missing_titles;
        """);
    Fixture("claude", "claude", hardware, $"""
        CREATE TABLE sessions(session_id,parent_session_id);
        INSERT INTO sessions VALUES ('root',NULL),('child','root'),('nested','child'),('outside',NULL);
        CREATE TABLE title_data(session_id,custom_title,ai_title);
        INSERT INTO title_data VALUES ('root','Custom','AI'),('child',NULL,'Child');
        CREATE VIEW current_sessions AS SELECT * FROM sessions LEFT JOIN title_data USING(session_id);
        CREATE TABLE transcript_events(session_id,created_at_utc_ms,row_type,prompt_source,cwd,git_branch,observation_id);
        INSERT INTO transcript_events VALUES ('root',{from - 1},'user','human','/outside','before',1),
            ('root',{from + 10},'user','human','/repo','main',2), ('root',{from + 11},'user',NULL,NULL,'feature',3),
            ('root',{from + 12},'assistant',NULL,'/new',NULL,4), ('child',{from + 5},'user','human','/child',NULL,5),
            ('nested',{from + 50},'user','human','/nested',NULL,6),('outside',{to},'user','human',NULL,NULL,7);
        """);
    Fixture("codex", "codex", hardware, $"""
        CREATE TABLE threads(thread_id,parent_thread_id,cwd,git_branch);
        INSERT INTO threads VALUES ('root',NULL,'/project','main'),('child','root','/subproject',NULL),('outside',NULL,NULL,NULL);
        CREATE TABLE thread_names(thread_id,name);
        INSERT INTO thread_names VALUES ('root',''),('child','Child');
        CREATE VIEW current_threads AS SELECT * FROM threads LEFT JOIN thread_names USING(thread_id);
        CREATE TABLE rollout_events(thread_id,completed_at_utc_ms,recorded_at_utc_ms,started_at_utc_ms,item_type);
        INSERT INTO rollout_events VALUES ('root',{from + 65},{from - 1},{from - 2},'UserMessage'),
            ('root',NULL,{from + 60},{from - 1},'AgentMessage'),('root',NULL,NULL,{from + 55},'UserMessage'),
            ('child',NULL,{from + 80},NULL,'UserMessage'),('outside',{to},{to - 1},NULL,'UserMessage');
        """);

    var rules = Path.Combine(store.Root, "rules", "titles.json");
    Directory.CreateDirectory(Path.GetDirectoryName(rules)!);
    File.WriteAllText(rules, """
        {"rules":[
          {"field":"window_title","bundle_id":"com.apple.Terminal","pattern":" \\([0-9]+\\)$","replacement":"","why":"counter"},
          {"field":"tab_title","pattern":" [0-9]+$","replacement":"!"},
          {"field":"tab_title","pattern":"!$","replacement":""}
        ]}
        """);

    using var output = new StringWriter(CultureInfo.InvariantCulture);
    var commandLine = new[] { "day", "--store", store.Root, "--date", "2026-11-01", "--time-zone", "America/New_York" };
    Equal(0, await Commands.Create(output: output).Parse(commandLine).InvokeAsync(), "day command succeeds");
    using var report = JsonDocument.Parse(output.ToString());
    var root = report.RootElement;
    Equal("America/New_York", root.GetProperty("time_zone").GetString(), "requested time zone");
    var devices = root.GetProperty("devices").EnumerateArray().ToArray();
    Equal(4, devices.Length, "identities are grouped per host with each unknown database kept separate");
    var local = devices.Single(device => Text(device, "device") == $"macos:{hardware}");
    var remote = devices.Single(device => Text(device, "device") == "macos:different-device");
    Check(devices.TakeWhile(device => device.GetProperty("this_host").GetBoolean()).Contains(local),
        "this host sorts before other devices");
    Equal("Newest name", Text(local, "name"), "the latest host observation across databases supplies the name");
    Check(local.GetProperty("time_zones").EnumerateArray().Select(zone => zone.GetString())
        .SequenceEqual(["Asia/Shanghai", "Asia/Tokyo"]), "only time zones in effect during a run");
    var replica = local.GetProperty("databases").EnumerateArray().Single(db => Text(db, "database") == "replica");
    Check(replica.GetProperty("missing").EnumerateArray().Select(value => value.GetString())
        .ToHashSet().IsSupersetOf(["system_sleep_events", "screen_lock_events.is_locked", "capture_runs.confirmed_until_utc_ms"]),
        "missing table and columns are reported without losing the rest of a replica");
    Check(remote.GetProperty("databases")[0].GetProperty("missing").EnumerateArray().Any(value => value.GetString() == "history_pages"),
        "a view whose backing table is missing is reported");
    var unknown = devices.Single(device => Text(device, "device") == "db:unidentified");
    Check(devices.Any(device => Text(device, "device") == "db:null-hardware"), "NULL hardware also uses the file name");
    Check(unknown.GetProperty("this_host").GetBoolean(), "a missing identity follows DatabaseHost.IsThisHost");

    var presence = Rows(local, "presence");
    Check(presence.Select(row => (At(row, "start") - from, At(row, "end") - from, Text(row, "state"))).SequenceEqual([
        (0L,100L,"active"), (100L,120L,"locked"), (120L,180L,"asleep"), (180L,185L,"session_inactive"),
        (185L,190L,"locked"), (190L,200L,"active"), (200L,260L,"unknown"), (260L,340L,"active"),
        (340L,400L,"idle"), (400L,700L,"active"), (700L,800L,"unobserved"), (800L,900L,"active"),
        (900L,950L,"unobserved"), (950L,1050L,"idle"), (1050L,to-from,"unobserved"),
    ]), "presence precedence, channel recovery, null lock, adjacent merge and full coverage");
    var remotePresence = Rows(remote, "presence");
    Check(remotePresence.Length == 2 && At(remotePresence[1], "end") == to && Text(remotePresence[1], "state") == "active",
        "remote presence is separate and clipped at the next midnight");
    Equal(900L, local.GetProperty("summary").GetProperty("observed_ms").GetInt64(), "observed time");
    Equal(590L, local.GetProperty("summary").GetProperty("active_ms").GetInt64(), "active time");

    var attention = Rows(local, "attention");
    Equal("Task", Text(attention[0], "title"), "title rule applies before segmenting");
    Equal(from + 90, At(attention[0], "end"), "equal normalized window titles merge");
    Check(attention.All(segment => (At(segment, "end") <= from + 120 || At(segment, "start") >= from + 180)
        && (Text(segment, "bundle_id") == "com.google.Chrome") == segment.TryGetProperty("tab", out _)),
        "sleep excludes attention and tabs appear only for Chrome");
    Equal("Page", Text(AttentionAt(320).GetProperty("tab"), "title"), "focused profile tab uses normalized title");
    Check(At(AttentionAt(320), "start") == from + 300, "equal normalized tab titles merge");
    Equal("https://second.test", Text(AttentionAt(450).GetProperty("tab"), "url"), "Chrome tab switches with focus");
    Check(AttentionAt(480).GetProperty("tab").ValueKind == JsonValueKind.Null &&
        AttentionAt(495).GetProperty("tab").ValueKind == JsonValueKind.Null, "browser gap ends focus until a fresh observation");
    Equal("Next", Text(AttentionAt(510).GetProperty("tab"), "title"), "browser snapshot restores fresh focus");
    Check(AttentionAt(565).GetProperty("tab").ValueKind == JsonValueKind.Null, "browser run end prevents cross-run stale focus");
    Equal(null, Text(AttentionAt(90), "title"), "unavailable title is null");
    Equal(null, Text(AttentionAt(395), "title"), "title availability alone does not restore an old title");
    Equal("Browser", Text(AttentionAt(410), "title"), "fresh title restores the value");
    Equal("", Text(AttentionAt(620), "title"), "known no window has an empty title");
    Check(Text(AttentionAt(650), "app") is null && Text(AttentionAt(660), "app") == "Terminal",
        "restored applications channel waits for a new foreground value");
    Check(Text(AttentionAt(680), "app") is null && Text(AttentionAt(680), "title") is null, "no frontmost app clears the title");
    var apps = Rows(local.GetProperty("summary"), "apps");
    Equal("Chrome", Text(apps[0], "app"), "apps sorted by active time");
    Equal(240L, apps[0].GetProperty("active_ms").GetInt64(), "app active time overlaps presence");

    var sessions = Rows(local, "agent_sessions");
    Equal(2, sessions.Length, "sessions outside day excluded and children folded");
    Equal("claude", Text(sessions[0], "source"), "sessions sorted by start");
    CheckSession(sessions[0], 5, 50, 1, 2, "Custom", "/new", "feature");
    CheckSession(sessions[1], 55, 80, 2, 1, null, "/project", "main");
    var visits = Rows(local, "visits");
    Equal(1, visits.Length, "only local visits in the half-open day");
    Equal("Latest", Text(visits[0], "title"), "visit title joins within the profile");
    var downloads = Rows(local, "downloads");
    Equal(1, downloads.Length, "downloads use the half-open start date");
    Equal("https://first.test/final", Text(downloads[0], "url"), "latest download URL");
    Equal("/tmp/file", Text(downloads[0], "path"), "latest download path");
    Equal("complete", Text(downloads[0], "state"), "latest download state");

    foreach (var invalid in new[] {
        """{"rules":[{"field":"tab_title","pattern":"a","replacement":""},{"field":"tab_title","pattern":"[","replacement":""}]}""",
        """{"rules":[{"field":"tab_title","pattern":"a","replacement":""},{"field":"other","pattern":".","replacement":""}]}""",
        """{"rules":[{"field":"tab_title","pattern":"a","replacement":""},{""",
    })
    {
        File.WriteAllText(rules, invalid);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var failed = await Process.RunAndCaptureTextAsync(RunProcess.Dotnet,
            ["Host/Cli/bin/Debug/net11.0/orisumi.dll", .. commandLine], timeout.Token);
        Check(failed.ExitStatus is { ExitCode: 1, Canceled: false } && failed.StandardError.Contains("规则索引 1") &&
            failed.StandardOutput.Length == 0, "invalid rules fail with their zero-based index and no JSON output");
    }
    Console.WriteLine("PASS: day intervals, daylight saving, device grouping, presence, attention, agents, browser history and rules");

    JsonElement AttentionAt(long time) =>
        attention.Single(row => At(row, "start") <= from + time && At(row, "end") > from + time);

    void CheckSession(JsonElement session, long start, long end, long prompts, int children, string? title, string cwd, string branch)
    {
        Equal("root", Text(session, "id"), "root ID");
        Equal(from + start, At(session, "start"), "first event or subagent event");
        Equal(from + end, At(session, "end"), "last event or subagent event");
        Equal(prompts, session.GetProperty("prompts").GetInt64(), "only root prompts");
        Equal(children, session.GetProperty("subagents").GetInt32(), "distinct descendants with events");
        Equal(title, Text(session, "title"), "root title");
        Equal(cwd, Text(session, "cwd"), "root cwd");
        Equal(branch, Text(session, "git_branch"), "root branch");
    }

    void Fixture(string source, string id, string? uuid, string sql, long nameAt = 0, string name = "Device")
    {
        var path = Path.Combine(temp.FullName, id + ".sqlite");
        using (var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            database.Open();
            using var command = database.CreateCommand();
            if (uuid is not null)
            {
                command.CommandText = """
                    CREATE TABLE database_identity(platform,hardware_uuid);
                    INSERT INTO database_identity VALUES ('macos',$uuid);
                    CREATE TABLE host_observations(observation_id,observed_at_utc_ms,host_name);
                    INSERT INTO host_observations VALUES (1,$at,$name);
                    """;
                command.Parameters.AddWithValue("$uuid", uuid);
                command.Parameters.AddWithValue("$at", nameAt);
                command.Parameters.AddWithValue("$name", name);
                command.ExecuteNonQuery();
                command.Parameters.Clear();
            }
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        Directory.CreateDirectory(store.Observations(source));
        File.Move(path, Path.Combine(store.Observations(source), id + ".sqlite"));
    }
}
finally { temp.Delete(recursive: true); }

static JsonElement[] Rows(JsonElement row, string field) => row.GetProperty(field).EnumerateArray().ToArray();
static string? Text(JsonElement row, string field) => row.GetProperty(field).GetString();
static long At(JsonElement row, string field) =>
    DateTimeOffset.Parse(Text(row, field)!, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Equal<T>(T expected, T actual, string message) =>
    Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}. Expected {expected}, got {actual}");
