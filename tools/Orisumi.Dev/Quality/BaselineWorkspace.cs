namespace Orisumi.Dev;

internal sealed record BaselineWorkspace(string Path)
{
    private string RestoreMarker => System.IO.Path.Combine(Path, ".baseline-restored");

    public bool NeedsRestore => !File.Exists(RestoreMarker);

    public void MarkRestored() => File.WriteAllText(RestoreMarker, "restored");

    public static async Task<BaselineWorkspace> PrepareAsync(RepositoryContext repository, string commit, Run run, CancellationToken cancellationToken)
    {
        var path = repository.Path(".artifacts", "quality-baselines", commit);
        if (Directory.Exists(path) && Directory.EnumerateFiles(path, "*.slnx").Any())
        {
            run.Commands.Add($"reuse cached baseline worktree .artifacts/quality-baselines/{commit[..7]}");
            return new BaselineWorkspace(path);
        }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        run.Commands.Add($"git worktree add --detach .artifacts/quality-baselines/{commit[..7]} {commit[..7]}");
        var added = await repository.Git.AddWorktreeAsync(path, commit, cancellationToken);
        if (added.ExitCode != 0) throw new InvalidDataException($"Could not materialize Git base '{commit[..7]}': {added.LastLine}");
        return new BaselineWorkspace(path);
    }
}
