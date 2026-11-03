namespace Orisumi.Capturing;

internal static class CodexCapture
{
    private const string TimestampPrefix = "{\"timestamp\":\"";

    public static async Task<Capture> RunAsync(Store store, string home, TimeRange range)
    {
        var writer = store.BeginCapture("codex", home, range);
        foreach (var folder in new[] { "sessions", "archived_sessions" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(home, folder), "*.jsonl", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                if (File.GetLastWriteTimeUtc(file) >= range.From.UtcDateTime && Touches(file, range))
                    writer.Copy(file, Path.GetRelativePath(home, file));
        }
        writer.Copy(Path.Combine(home, "session_index.jsonl"), "session_index.jsonl");
        var state = Path.Combine(home, "state_5.sqlite");
        foreach (var table in new[] { "threads", "thread_spawn_edges" })
            await Shell.ToFileAsync("sqlite3", ["-readonly", "-json", state, $"select * from {table}"], null,
                writer.Target($"state_5/{table}.json", $"{state}#{table}"));
        return writer.Complete();
    }

    private static bool Touches(string file, TimeRange range) =>
        JsonLines.Read(file).Any(entry => entry.Text.StartsWith(TimestampPrefix, StringComparison.Ordinal)
            && range.Contains(Clock.Parse(entry.Text.Substring(TimestampPrefix.Length, 24))));
}
