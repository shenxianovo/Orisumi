namespace Orisumi.Capturing;

public static class ClaudeCodeSource
{
    public static CaptureSource Create(string? projects = null, string? desktopSessions = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        projects = Path.GetFullPath(projects ?? Path.Combine(home, ".claude", "projects"));
        desktopSessions = Path.GetFullPath(desktopSessions ?? Path.Combine(home, "Library", "Application Support", "Claude", "claude-code-sessions"));
        return new CaptureSource("claude-code", $"projects: {projects}\ndesktop-sessions: {desktopSessions}",
            [new FileRoot(projects, "projects"), new FileRoot(desktopSessions, "desktop-sessions")]);
    }
}
