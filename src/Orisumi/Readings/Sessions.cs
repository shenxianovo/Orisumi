using System.Text.Json;

namespace Orisumi.Readings;

internal sealed record CodexThread(string Id, string Cwd, string Title, string? ThreadSource);

internal static class Sessions
{
    public static IEnumerable<CapturedFile> ClaudeLogs(Capture capture) =>
        capture.Files.Where(file => file.Path.StartsWith("projects/", StringComparison.Ordinal) && file.Path.EndsWith(".jsonl", StringComparison.Ordinal));

    public static bool IsClaudeSubagent(CapturedFile file) => file.Path.Contains("/subagents/", StringComparison.Ordinal);

    public static IEnumerable<CapturedFile> CodexRollouts(Capture capture) =>
        capture.Files.Where(file => Path.GetFileName(file.Path).StartsWith("rollout-", StringComparison.Ordinal));

    public static string CodexThreadId(CapturedFile file)
    {
        var name = Path.GetFileNameWithoutExtension(file.Path)["rollout-YYYY-MM-DDTHH-MM-SS-".Length..];
        return name.Split('_')[0];
    }

    public static IReadOnlyDictionary<string, CodexThread> CodexThreads(Store store, Capture capture)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (_, text) in JsonLines.Read(store.PathOf(capture, "session_index.jsonl")))
        {
            var entry = Json.Parse(text);
            if (entry.Text("id") is { } id && entry.Text("thread_name") is { } name) names[id] = name;
        }
        using var document = JsonDocument.Parse(File.ReadAllText(store.PathOf(capture, "state_5/threads.json")));
        return document.RootElement.Items()
            .Select(row => new CodexThread(row.Text("id")!, row.Text("cwd") ?? "",
                names.GetValueOrDefault(row.Text("id")!) ?? row.Text("title") ?? "", row.Text("thread_source")))
            .ToDictionary(thread => thread.Id, StringComparer.Ordinal);
    }
}
