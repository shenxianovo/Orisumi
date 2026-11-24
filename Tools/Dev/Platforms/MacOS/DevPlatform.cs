using System.CommandLine;
using Orisumi.Desktop;
using Orisumi.Platform;
using static Orisumi.Dev.CheckCommand;

namespace Orisumi.Dev;

internal static class DevPlatform
{
    internal static void AddCommands(RootCommand root)
    {
        var host = new MacHostBuilder(HostBundle.Tools.Current);
        static ICollectorJobs Jobs(Instance instance) => new MacCollectorJobs(instance, SystemLocations.Current);
        root.Add(Deploy.Create(Platforms.CurrentRid, Jobs, host));
        root.Add(ReleaseCommand.Create(Platforms.CurrentRid, host));
        root.Add(DevInstance.ResetCommand(Jobs));
    }

    internal static bool HasToolsPath(string tools) => (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Any(entry => Path.TrimEndingDirectorySeparator(entry) == tools);

    internal static (string, string[]) Npm(string[] arguments) => ("npm", arguments);

    internal static string[] BuildTargets(string checkout) => ["Orisumi.slnx"];

    internal static async Task CheckPrerequisites(List<string> missing, CancellationToken token)
    {
        // Command Line Tools lack SwiftUI's macro plugins, so the GUI only builds with Xcode selected.
        var developer = await SetupCommand.Output("xcode-select", token, "-p");
        if (developer is null || !developer.Trim().EndsWith(".app/Contents/Developer", StringComparison.Ordinal))
            missing.Add($"Xcode：安装后运行 sudo xcode-select -s /Applications/Xcode.app（当前 {developer?.Trim() ?? "未安装"}）。");
    }

    internal static (string Name, (string Executable, string[] Arguments)[] Steps)[] NativeChecks(string temporary, string host)
    {
        var packages = Path.Combine(temporary, "packages");
        var tools = Path.Combine(temporary, "tools");
        return
        [
            FileCheck("CaptureStartup"),
            FileCheck("CaptureStatus"),
            FileCheck("Schema"),
            FileCheck("CaptureIntegration"),
            FileCheck("BrowserIntegration"),
            FileCheck("CaptureCoverage"),
            FileCheck("Day"),
            FileCheck("Export"),
            FileCheck("ClaudeIncremental"),
            FileCheck("CodexIncremental"),
            FileCheck("MacOSObservation"),
            FileCheck("Instance"),
            ("App.MacOS", [("swift", ["test", "--package-path", MacHostBuilder.AppPackage])]),
            ("SourceLauncher", [Dotnet("pack", Repository.DevProject, "-c", "Release", "-o", packages),
                Dotnet("tool", "install", "Orisumi.Dev", "--tool-path", tools, "--source", packages),
                RunFile("SourceLauncher", Path.Combine(tools, "orisumi-dev"), host)]),
        ];
    }
}
