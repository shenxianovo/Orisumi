namespace Orisumi.Cli;

internal static class Repository
{
    internal const string CliProject = "src/Orisumi.Cli/Orisumi.Cli.csproj";

    internal static string? Find(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Orisumi.slnx")) &&
                File.Exists(Path.Combine(directory.FullName, CliProject)))
                return directory.FullName;
            // A nested, unrelated checkout owns its own commands.
            if (Path.Exists(Path.Combine(directory.FullName, ".git")))
                break;
        }
        return null;
    }

    internal static string Require() => Find(Environment.CurrentDirectory) ??
        throw new DirectoryNotFoundException("请在 Orisumi 仓库或其子目录中运行 orisumi。");
}
