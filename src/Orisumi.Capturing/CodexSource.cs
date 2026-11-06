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
            new FileRoot(Path.Combine(home, "dictation-history"), "dictation-history", Optional: true),
            new FileRoot(Path.Combine(home, "automations"), "automations", Optional: true),
        ])
        {
            Files =
            [
                new FileInput(Path.Combine(home, "session_index.jsonl"), "session_index.jsonl"),
                new FileInput(Path.Combine(home, "state_5.sqlite"), "state_5.sqlite", FileInputMethod.SqliteBackup),
                new FileInput(Path.Combine(home, "logs_2.sqlite"), "logs_2.sqlite", FileInputMethod.SqliteBackup, Optional: true),
                new FileInput(Path.Combine(home, "goals_1.sqlite"), "goals_1.sqlite", FileInputMethod.SqliteBackup, Optional: true),
                new FileInput(Path.Combine(home, "thread_history_1.sqlite"), "thread_history_1.sqlite", FileInputMethod.SqliteBackup, Optional: true),
                new FileInput(Path.Combine(home, "sqlite", "codex.db"), "sqlite/codex.db", FileInputMethod.SqliteBackup, Optional: true),
                new FileInput(Path.Combine(home, "sqlite", "codex-dev.db"), "sqlite/codex-dev.db", FileInputMethod.SqliteBackup, Optional: true),
                new FileInput(Path.Combine(home, "queue_1.sqlite"), "queue_1.sqlite", FileInputMethod.SqliteBackup, Optional: true),
                new FileInput(Path.Combine(home, "memories_1.sqlite"), "memories_1.sqlite", FileInputMethod.SqliteBackup, Optional: true),
                new FileInput(Path.Combine(home, "sqlite", "codex-thread-summaries-dev.db"), "sqlite/codex-thread-summaries-dev.db", FileInputMethod.SqliteBackup, Optional: true),
                new FileInput(Path.Combine(home, "realtime-voice-continuity.json"), "realtime-voice-continuity.json", Optional: true),
                new FileInput(Path.Combine(home, ".codex-global-state.json"), ".codex-global-state.json", Optional: true),
            ],
        };
    }

    private static bool IsRollout(string path) => path.EndsWith(".jsonl", StringComparison.Ordinal)
        || path.EndsWith(".jsonl.zst", StringComparison.Ordinal);
}
