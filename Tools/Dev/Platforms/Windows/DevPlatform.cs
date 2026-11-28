using System.CommandLine;
using Orisumi.Desktop;
using Orisumi.Platform;

namespace Orisumi.Dev;

internal static class DevPlatform
{
    internal static void AddCommands(RootCommand root)
    {
        static ICollectorJobs Jobs(Instance instance) => new WindowsCollectorJobs(instance);
        root.Add(Deploy.Create(Platforms.CurrentRid, Jobs, new WindowsHostBuilder()));
        root.Add(DevInstance.ResetCommand(Jobs));
    }

    internal static IEnumerable<string> BuildTargets(string checkout) => Repository.ManagedProjects(checkout, Platforms.CurrentRid);

    internal static bool HasToolsPath(string tools) => PathEntries().Any(entry => Path.GetRelativePath(tools, entry) == ".");

    private static IEnumerable<string> PathEntries() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => Environment.ExpandEnvironmentVariables(entry.Trim('"'))).Where(entry => entry.Length > 0);

    internal static Task CheckPrerequisites(List<string> missing, CancellationToken token)
    {
        if (FindNpm() is null) missing.Add("npm：请安装包含 npm 的 Node.js 并加入 PATH。");
        return Task.CompletedTask;
    }

    internal static (string, string[]) Npm(string[] arguments)
    {
        var (executable, script) = FindNpm() ?? throw new FileNotFoundException("在 PATH 中找不到 npm。");
        return (executable, script is null ? arguments : [script, .. arguments]);
    }

    private static (string Executable, string? Script)? FindNpm()
    {
        foreach (var entry in PathEntries())
        {
            var directory = Path.GetFullPath(entry);
            var shim = Path.Combine(directory, "npm.exe");
            if (File.Exists(shim)) return (shim, null);

            // The Node.js distribution's npm.cmd starts this script with its neighboring node.exe.
            var cli = Path.Combine(directory, "node_modules", "npm", "bin", "npm-cli.js");
            if (!File.Exists(cli)) continue;
            var node = Path.Combine(directory, "node.exe");
            return (File.Exists(node) ? node : "node", cli);
        }
        return null;
    }

    internal static (string Name, (string Executable, string[] Arguments)[] Steps)[] NativeChecks() =>
        [CheckCommand.FileCheck("WindowsSupervisor"), CheckCommand.FileCheck("WindowsObservation")];
}
