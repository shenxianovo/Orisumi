using System.Globalization;
using System.Text.Json;
using Orisumi.Capturing;

namespace Orisumi.Readings;

internal sealed record TimelineEntry(DateTimeOffset Start, DateTimeOffset End, string Device, string Kind, string Label, string Position);

internal sealed record InputCount(int Keys, int Clicks, int Scrolls, int Line);

internal static class TimelineReading
{
    public const string Name = "timeline";

    public static readonly TimeSpan Bucket = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan SessionPause = TimeSpan.FromMinutes(30);

    public static IEnumerable<TimelineEntry> Read(Store store, Capture heartbeat, Capture claude, Capture codex, Capture git) =>
        Heartbeat(store, heartbeat)
            .Concat(ClaudeSessions(store, claude))
            .Concat(CodexSessions(store, codex))
            .Concat(Commits(store, git))
            .OrderBy(span => span.Start);

    public static IReadOnlyDictionary<string, (string Device, string Source)> Streams(Store store, Capture capture)
    {
        var devices = Rows(store, capture, "subjects").ToDictionary(row => row.Text("SubjectId")!, row => row.Text("DisplayName") ?? "?", StringComparer.Ordinal);
        return Rows(store, capture, "streams").ToDictionary(
            row => row.Text("StreamId")!,
            row => (devices.GetValueOrDefault(row.Text("SubjectId")!, "?"), row.Text("Source")!),
            StringComparer.Ordinal);
    }

    private static IEnumerable<JsonElement> Rows(Store store, Capture capture, string table) =>
        JsonLines.Read(store.PathOf(capture, $"{table}.jsonl")).Select(entry => Json.Parse(entry.Text));

    private static IEnumerable<TimelineEntry> Heartbeat(Store store, Capture capture)
    {
        var streams = Streams(store, capture);
        var apps = Rows(store, capture, "apps").ToDictionary(row => row.Child("Id").GetInt64(), row => row.Text("DisplayName") ?? "?");
        var identities = Rows(store, capture, "app-identities").ToDictionary(
            row => row.Child("Id").GetInt64(),
            row => $"{apps.GetValueOrDefault(row.Child("AppId").GetInt64()) ?? "?"}（{row.Text("Key")}）");
        foreach (var (line, text) in JsonLines.Read(store.PathOf(capture, "segments.jsonl")))
        {
            var row = Json.Parse(text);
            var (device, source) = streams.GetValueOrDefault(row.Text("StreamId")!, ("?", "?"));
            var payload = row.Child("Payload");
            var app = row.Child("AppIdentityId").ValueKind == JsonValueKind.Number ? identities.GetValueOrDefault(row.Child("AppIdentityId").GetInt64()) : null;
            var (kind, label) = row.Text("Source") switch
            {
                "browser" => ("浏览器", $"{payload.Text("title")} · {payload.Child("attributes").Text("url")}"),
                "vrchat.account" => ("VRChat", payload.Child("attributes").Text("worldName") ?? payload.Text("title") ?? ""),
                _ => ("前台", $"{app ?? "?"} · {payload.Text("title")}"),
            };
            yield return new(Clock.Parse(row.Text("StartTime")!), Clock.Parse(row.Text("EndTime")!), source == "vrchat.account" ? "VRChat 账号" : device,
                kind, label, capture.At("segments.jsonl", line).ToString());
        }
        foreach (var span in Input(store, capture, streams)) yield return span;
    }

    private static IEnumerable<TimelineEntry> Input(Store store, Capture capture, IReadOnlyDictionary<string, (string Device, string Source)> streams)
    {
        var buckets = new SortedDictionary<(DateTimeOffset Start, string Device), InputCount>();
        foreach (var (line, text) in JsonLines.Read(store.PathOf(capture, "events.jsonl")))
        {
            var row = Json.Parse(text);
            var at = Clock.Parse(row.Text("Timestamp")!);
            var start = new DateTimeOffset(at.UtcTicks - (at.UtcTicks % Bucket.Ticks), TimeSpan.Zero);
            var device = streams.TryGetValue(row.Text("StreamId")!, out var stream) ? stream.Device : "?";
            var key = (start, device);
            var count = buckets.GetValueOrDefault(key) ?? new InputCount(0, 0, 0, line);
            buckets[key] = row.Child("Payload").Text("eventType") switch
            {
                "keyDown" => count with { Keys = count.Keys + 1 },
                "mouseButton" => count with { Clicks = count.Clicks + 1 },
                _ => count with { Scrolls = count.Scrolls + 1 },
            };
        }
        return buckets.Select(entry => new TimelineEntry(entry.Key.Start, entry.Key.Start + Bucket, entry.Key.Device, "键鼠",
            $"按键 {entry.Value.Keys} · 点击 {entry.Value.Clicks} · 滚轮 {entry.Value.Scrolls}",
            capture.At("events.jsonl", entry.Value.Line).ToString()));
    }

    private static IEnumerable<TimelineEntry> ClaudeSessions(Store store, Capture capture)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Sessions.ClaudeLogs(capture).Where(file => !Sessions.IsClaudeSubagent(file)))
        {
            var title = "";
            var cwd = "";
            var moments = new List<(DateTimeOffset At, int Line)>();
            foreach (var (line, text) in JsonLines.Read(store.PathOf(capture, file.Path)))
            {
                var record = Json.Parse(text);
                if (record.Text("type") == "custom-title") title = record.Text("customTitle") ?? title;
                if (record.Text("cwd") is { } directory) cwd = directory;
                if (record.Text("timestamp") is not { } timestamp || record.Text("uuid") is not { } uuid || !seen.Add(uuid)) continue;
                var at = Clock.Parse(timestamp);
                if (capture.Range.Contains(at)) moments.Add((at, line));
            }
            foreach (var span in Stretches(moments, capture, file.Path, "Claude Code 会话", $"{title} · {cwd}")) yield return span;
        }
    }

    private static IEnumerable<TimelineEntry> CodexSessions(Store store, Capture capture)
    {
        var threads = Sessions.CodexThreads(store, capture);
        foreach (var file in Sessions.CodexRollouts(capture))
        {
            var thread = threads.GetValueOrDefault(Sessions.CodexThreadId(file));
            if (thread?.ThreadSource is not (null or "user")) continue;
            var moments = new List<(DateTimeOffset At, int Line)>();
            foreach (var (line, text) in JsonLines.Read(store.PathOf(capture, file.Path)))
            {
                var record = Json.Parse(text);
                if (record.Child("metadata").IsTrue("inherited_user_message") || record.Text("timestamp") is not { } timestamp) continue;
                var at = Clock.Parse(timestamp);
                if (capture.Range.Contains(at)) moments.Add((at, line));
            }
            foreach (var span in Stretches(moments, capture, file.Path, "Codex 会话", $"{thread?.Title} · {thread?.Cwd}")) yield return span;
        }
    }

    private static IEnumerable<TimelineEntry> Stretches(List<(DateTimeOffset At, int Line)> moments, Capture capture, string file, string kind, string label)
    {
        if (moments.Count == 0) yield break;
        moments.Sort((left, right) => left.At.CompareTo(right.At));
        var start = moments[0];
        var last = moments[0];
        foreach (var moment in moments.Skip(1))
        {
            if (moment.At - last.At > SessionPause)
            {
                yield return new(start.At, last.At, capture.Host, kind, label, capture.At(file, start.Line).ToString());
                start = moment;
            }
            last = moment;
        }
        yield return new(start.At, last.At, capture.Host, kind, label, capture.At(file, start.Line).ToString());
    }

    private static IEnumerable<TimelineEntry> Commits(Store store, Capture capture)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in capture.Files)
        {
            foreach (var (line, text) in JsonLines.Read(store.PathOf(capture, file.Path)))
            {
                var commit = JsonSerializer.Deserialize<CommitRecord>(text, Store.Json)!;
                if (!seen.Add(commit.Sha)) continue;
                var headers = commit.Commit.Split("\n\n", 2)[0].Split('\n');
                var subject = commit.Commit.Split("\n\n", 2).ElementAtOrDefault(1)?.Split('\n')[0] ?? "";
                var author = headers.First(header => header.StartsWith("author ", StringComparison.Ordinal)).Split(' ');
                var at = DateTimeOffset.FromUnixTimeSeconds(long.Parse(author[^2], CultureInfo.InvariantCulture));
                if (!capture.Range.Contains(at)) continue;
                var changes = commit.Numstat.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(change => change.Split('\t')).Where(change => change.Length == 3).ToList();
                var added = changes.Sum(change => int.TryParse(change[0], out var count) ? count : 0);
                var removed = changes.Sum(change => int.TryParse(change[1], out var count) ? count : 0);
                yield return new(at, at, commit.Repository, "提交",
                    $"{commit.Sha[..8]} {subject} · {changes.Count} 个文件 +{added} -{removed}", capture.At(file.Path, line).ToString());
            }
        }
    }
}
