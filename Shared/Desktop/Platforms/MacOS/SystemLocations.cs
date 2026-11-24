using System.Runtime.Versioning;

namespace Orisumi.Platform;

[SupportedOSPlatform("macos")]
internal sealed record SystemLocations(string Agents, string Commands, string Launchctl)
{
    internal static SystemLocations Current { get; } = ForUser(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    private static SystemLocations ForUser(string home) => new(
        Path.Combine(home, "Library", "LaunchAgents"),
        Path.Combine(home, ".local", "bin"),
        "/bin/launchctl");
}
