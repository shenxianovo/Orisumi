namespace Orisumi.Dev;

internal sealed record BaselineWorkspace(string Path, bool Reused)
{
    private string RestoreMarker => System.IO.Path.Combine(Path, ".baseline-restored");

    public bool NeedsRestore => !File.Exists(RestoreMarker);

    public void MarkRestored() => File.WriteAllText(RestoreMarker, "restored");
}

internal sealed class BaselineWorkspaceCache(RepositoryContext repository, IProcessRunner runner)
{
    public string Root { get; } = repository.Path(".artifacts", "quality-baselines");

    public async Task<(BaselineWorkspace? Workspace, string? Error)> PrepareAsync(
        string commit,
        ICollection<string> commands,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Root);
        var resolved = await ResolveAsync(commit, cancellationToken);
        if (resolved is null)
        {
            return (null, $"Could not resolve Git base '{commit}' to a commit.");
        }
        var path = System.IO.Path.Combine(Root, resolved);
        if (Directory.Exists(path) && Directory.EnumerateFiles(path, "*.slnx").Any())
        {
            commands.Add($"reuse cached baseline worktree .artifacts/quality-baselines/{resolved[..7]}");
            return (new BaselineWorkspace(path, true), null);
        }

        var added = await AddAsync(path, resolved, cancellationToken);
        if (added.ExitCode != 0)
        {
            await runner.CaptureAsync("git", ["worktree", "prune"], null, cancellationToken);
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            added = await AddAsync(path, resolved, cancellationToken);
        }
        commands.Add($"git worktree add --detach .artifacts/quality-baselines/{resolved[..7]} {resolved[..7]}");
        return added.ExitCode == 0
            ? (new BaselineWorkspace(path, false), null)
            : (null, $"Could not materialize Git base '{resolved[..7]}': {LastUsefulLine(added)}");
    }

    private async Task<string?> ResolveAsync(string revision, CancellationToken cancellationToken)
    {
        var result = await runner.CaptureAsync(
            "git", ["rev-parse", "--verify", $"{revision}^{{commit}}"], null, cancellationToken);
        var candidate = result.StdOut.Trim();
        return result.ExitCode == 0 && candidate.Length == 40 ? candidate : null;
    }

    private Task<ProcessResult> AddAsync(string path, string commit, CancellationToken cancellationToken) =>
        runner.CaptureAsync("git", ["worktree", "add", "--detach", path, commit], null, cancellationToken);

    private static string LastUsefulLine(ProcessResult result) =>
        (result.StdErr + Environment.NewLine + result.StdOut)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault()?.Trim() ?? "command failed";
}
