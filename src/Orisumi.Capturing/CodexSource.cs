namespace Orisumi.Capturing;

public static class CodexSource
{
    public static CaptureSource Create(string? home = null)
    {
        home = Path.GetFullPath(home ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"));
        return new CaptureSource("codex", home,
        [
            new FileRoot(Path.Combine(home, "sessions"), "sessions", IsRollout),
            new FileRoot(Path.Combine(home, "archived_sessions"), "archived_sessions", IsRollout, Optional: true),
            new FileRoot(Path.Combine(home, "attachments"), "attachments", Optional: true),
            new FileRoot(Path.Combine(home, "generated_images"), "generated_images", Optional: true),
            new FileRoot(Path.Combine(home, "visualizations"), "visualizations", Optional: true),
        ])
        {
            Files =
            [
                new FileInput(Path.Combine(home, "session_index.jsonl"), "session_index.jsonl"),
                new FileInput(Path.Combine(home, "state_5.sqlite"), "state_5.sqlite", FileInputMethod.SqliteBackup),
            ],
        };
    }

    private static bool IsRollout(string path) => path.EndsWith(".jsonl", StringComparison.Ordinal)
        || path.EndsWith(".jsonl.zst", StringComparison.Ordinal);
}
