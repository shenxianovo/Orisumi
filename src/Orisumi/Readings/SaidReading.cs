namespace Orisumi.Readings;

internal sealed record Utterance(DateTimeOffset At, string Source, string Session, string Title, string Cwd, string Text, string Position);

internal static class SaidReading
{
    public const string Name = "said";

    public static IEnumerable<Utterance> Read(Store store, Capture claude, Capture codex) =>
        FromClaude(store, claude).Where(utterance => claude.Range.Contains(utterance.At))
            .Concat(FromCodex(store, codex).Where(utterance => codex.Range.Contains(utterance.At))).OrderBy(utterance => utterance.At);

    private static IEnumerable<Utterance> FromClaude(Store store, Capture capture)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        var found = new List<Utterance>();
        foreach (var file in Sessions.ClaudeLogs(capture))
        {
            foreach (var (line, text) in JsonLines.Read(store.PathOf(capture, file.Path)))
            {
                if (text.Contains("\"custom-title\"", StringComparison.Ordinal))
                {
                    var title = Json.Parse(text);
                    if (title.Text("type") == "custom-title") titles[title.Text("sessionId")!] = title.Text("customTitle") ?? "";
                    continue;
                }
                if (!text.Contains("\"human\"", StringComparison.Ordinal)) continue;
                var record = Json.Parse(text);
                if (record.Text("type") != "user" || record.Child("origin").Text("kind") != "human") continue;
                if (record.Text("uuid") is { } uuid && !seen.Add(uuid)) continue;
                var said = Json.Blocks(record.Child("message").Child("content")).Trim();
                if (said.Length == 0) continue;
                found.Add(new(Clock.Parse(record.Text("timestamp")!), capture.Source, record.Text("sessionId")!, "",
                    record.Text("cwd") ?? "", said, capture.At(file.Path, line).ToString()));
            }
        }
        return found.Select(utterance => utterance with { Title = titles.GetValueOrDefault(utterance.Session, "") });
    }

    private static IEnumerable<Utterance> FromCodex(Store store, Capture capture)
    {
        var threads = Sessions.CodexThreads(store, capture);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Sessions.CodexRollouts(capture))
        {
            var thread = threads.GetValueOrDefault(Sessions.CodexThreadId(file));
            if (thread?.ThreadSource is not (null or "user")) continue;
            foreach (var (line, text) in JsonLines.Read(store.PathOf(capture, file.Path)))
            {
                if (!text.Contains("\"user.text\"", StringComparison.Ordinal)) continue;
                var record = Json.Parse(text);
                var payload = record.Child("payload");
                if (record.Text("type") != "response_item" || payload.Text("type") != "message" || payload.Text("role") != "user") continue;
                if (record.Child("metadata").IsTrue("inherited_user_message")) continue;
                var kinds = payload.Child("internal_chat_message_metadata_passthrough").Child("content_item_kinds").Items().Select(kind => kind.GetString()).ToList();
                var said = string.Join('\n', payload.Child("content").Items()
                    .Where((_, index) => index < kinds.Count && kinds[index] == "user.text")
                    .Select(part => part.Text("text"))).Trim();
                if (said.Length == 0) continue;
                var at = Clock.Parse(record.Text("timestamp")!);
                var session = Sessions.CodexThreadId(file);
                if (!seen.Add($"{session}|{Clock.Iso(at)}|{said}")) continue;
                yield return new(at, capture.Source, session, thread?.Title ?? "", thread?.Cwd ?? "", said, capture.At(file.Path, line).ToString());
            }
        }
    }
}
