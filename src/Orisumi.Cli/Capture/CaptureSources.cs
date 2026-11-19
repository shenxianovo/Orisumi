namespace Orisumi.Cli.Capture;

// The only list of collectors. Each writes its databases under the store's observations/<Name>/ and exposes capture_coverage.
// Agent holds the arguments of the resident LaunchAgent dev.orisumi.capture.<Name> that `capture start` installs.
internal sealed record CaptureSource(string Name, string[]? Agent = null);

internal static class CaptureSources
{
    internal static readonly CaptureSource[] All =
    [
        new("claude", ["claude", "capture"]),
        new("codex", ["codex", "capture"]),
        // Chrome starts the native host for each message; register it with --extension-id.
        new("browser"),
        // Started detached from the caller to inherit its window and input permissions.
        new("macos"),
    ];
}
