namespace Orisumi.Dev;

internal sealed class Git(string root)
{
    private readonly ProcessRunner _runner = new(root);

    public static async Task<string> TopLevelAsync(string start)
    {
        var result = await new ProcessRunner(start).CaptureAsync(new("git", ["rev-parse", "--show-toplevel"]), CancellationToken.None);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
            throw new InvalidOperationException("orisumi must run inside a Git checkout.");
        return Path.GetFullPath(result.StdOut.Trim());
    }

    public async Task<string> BaseAsync(string? requested, CancellationToken cancellationToken)
    {
        if (requested is not null) return requested;
        if (string.IsNullOrWhiteSpace(await RunAsync(["status", "--porcelain"], cancellationToken)))
            throw new CommandUsageException("Changed scope needs --base when the worktree is clean.");
        return "HEAD";
    }

    public async Task<string> CommitAsync(string revision, CancellationToken cancellationToken)
    {
        var result = await CaptureAsync(["rev-parse", "--verify", $"{revision}^{{commit}}"], cancellationToken);
        if (result.ExitCode != 0) throw new CommandUsageException($"Invalid Git base '{revision}'.");
        return result.StdOut.Trim();
    }

    public async Task<IReadOnlyList<string>> ChangedPathsAsync(string baseRef, CancellationToken cancellationToken)
    {
        var tracked = await CaptureAsync(["diff", "--no-renames", "--name-only", "-z", baseRef, "--"], cancellationToken);
        if (tracked.ExitCode != 0) throw new CommandUsageException($"Invalid Git base '{baseRef}': {tracked.StdErr.Trim()}");
        var untracked = await RunAsync(["ls-files", "--others", "--exclude-standard", "-z"], cancellationToken);
        return Paths(tracked.StdOut + untracked);
    }

    public async Task<IReadOnlyList<string>> FilesAsync(CancellationToken cancellationToken) =>
        Paths(await RunAsync(["ls-files", "--cached", "--others", "--exclude-standard", "-z"], cancellationToken));

    public async Task<string?> ShowAsync(string revision, string path, CancellationToken cancellationToken)
    {
        var result = await CaptureAsync(["show", $"{revision}:{path}"], cancellationToken);
        return result.ExitCode == 0 ? result.StdOut : null;
    }

    public async Task<ProcessResult> AddWorktreeAsync(string path, string commit, CancellationToken cancellationToken)
    {
        var added = await CaptureAsync(["worktree", "add", "--detach", path, commit], cancellationToken);
        if (added.ExitCode == 0) return added;
        await CaptureAsync(["worktree", "prune"], cancellationToken);
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        return await CaptureAsync(["worktree", "add", "--detach", path, commit], cancellationToken);
    }

    private Task<ProcessResult> CaptureAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        _runner.CaptureAsync(new("git", arguments), cancellationToken);

    private async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await CaptureAsync(arguments, cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.StdErr.Trim());
        return result.StdOut;
    }

    private static string[] Paths(string output) =>
        [.. output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
}
