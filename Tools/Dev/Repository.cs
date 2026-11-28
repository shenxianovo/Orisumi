using Orisumi.Core;

namespace Orisumi.Dev;

internal static class Repository
{
    internal const string DevProject = "Tools/Dev/Orisumi.Dev.csproj";
    internal const string SupervisorProject = "Host/Platforms/Windows/Supervisor/Orisumi.Supervisor.Windows.csproj";
    internal const string AppProject = "Host/Platforms/Windows/App/Orisumi.App.Windows.csproj";
    internal const string CliProject = "Host/Cli/Orisumi.Cli.csproj";
    internal const string CollectorsDirectory = "Collectors";
    internal const string CollectorProjectPrefix = "Orisumi.Collectors.";

    internal static IEnumerable<(string Component, string Project)> CollectorProjects(string checkout, string rid) =>
        Directory.EnumerateDirectories(Path.Combine(checkout, CollectorsDirectory))
            .Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)
            .Select(folder => (Component: folder.ToLowerInvariant(),
                Project: $"{CollectorsDirectory}/{folder}/{CollectorProjectPrefix}{folder}.csproj"))
            .Where(item => Platforms.Runs(item.Component, rid));

    internal static string[] HostProjects(string rid) =>
        Platforms.Name(rid) == "windows" ? [CliProject, SupervisorProject, AppProject] : [CliProject];

    internal static IEnumerable<string> ManagedProjects(string checkout, string rid) =>
        [DevProject, .. HostProjects(rid), .. CollectorProjects(checkout, rid).Select(item => item.Project)];

    internal static string? Find(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Orisumi.slnx")) &&
                File.Exists(Path.Combine(directory.FullName, DevProject)))
                return directory.FullName;
            // A nested, unrelated checkout owns its own commands.
            if (Path.Exists(Path.Combine(directory.FullName, ".git")))
                break;
        }
        return null;
    }

    internal static string Require() => Find(Environment.CurrentDirectory) ??
        throw new DirectoryNotFoundException("请在 Orisumi 仓库或其子目录中运行 orisumi-dev。");

    internal static async Task<string> Query(string executable, string directory, CancellationToken token, string[] arguments,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var code = await RunProcess.Run(executable, arguments, output, token, directory: directory, errors: errors, environment: environment);
        token.ThrowIfCancellationRequested();
        if (code != 0) throw new IOException($"{executable} {string.Join(' ', arguments)} 退出码 {code}：{errors.ToString().Trim()}");
        return output.ToString();
    }
}
