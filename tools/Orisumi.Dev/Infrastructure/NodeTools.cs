namespace Orisumi.Dev;

internal sealed class NodeTools(RepositoryContext repository, IProcessRunner runner)
{
    public string Directory { get; } = repository.Path("tools", "Orisumi.Dev", "node-tools");

    public string Bin(string name) =>
        Path.Combine(Directory, "node_modules", ".bin", OperatingSystem.IsWindows() ? name + ".cmd" : name);

    public string Script(string name) => Path.Combine(Directory, name);

    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        if (System.IO.Directory.Exists(Path.Combine(Directory, "node_modules"))) return;
        var installed = await runner.CaptureAsync(new("npm", ["ci", "--ignore-scripts"], Directory), cancellationToken);
        if (installed.ExitCode != 0)
            throw new InvalidOperationException($"Installing {Path.GetFileName(Directory)} failed: {installed.StdErr.Trim()}");
    }
}
