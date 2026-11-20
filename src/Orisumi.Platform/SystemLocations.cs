namespace Orisumi.Platform;

internal sealed record SystemLocations(string Agents, string ChromeManifests, string Commands, string Launchctl)
{
    internal static SystemLocations Current { get; } = ForUser(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    private static SystemLocations ForUser(string home) => new(
        Path.Combine(home, "Library", "LaunchAgents"),
        Path.Combine(home, "Library", "Application Support", "Google", "Chrome", "NativeMessagingHosts"),
        Path.Combine(home, ".local", "bin"),
        "/bin/launchctl");
}
