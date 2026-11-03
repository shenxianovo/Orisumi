namespace Orisumi.Dev;

internal static class ReviewSelection
{
    public static bool Reviewable(string path, IReadOnlyList<ReviewRule> rules) =>
        !path.StartsWith(".scratch/", StringComparison.Ordinal) && rules.Any(rule => rule.Covers(path));

    public static async Task<IReadOnlyList<ReviewTarget>> ChangedAsync(
        RepositoryContext repository, IProcessRunner runner, IReadOnlyList<ReviewRule> rules,
        string baseRef, IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        var targets = new List<ReviewTarget>();
        foreach (var path in paths.Where(path => Reviewable(path, rules)))
        {
            var file = repository.Path(path);
            if (!File.Exists(file)) continue;
            var before = await runner.CaptureAsync("git", ["show", $"{baseRef}:{path}"], null, cancellationToken);
            var passages = Passages.Added(before.ExitCode == 0 ? before.StdOut : null, await File.ReadAllTextAsync(file, cancellationToken));
            if (passages.Count > 0) targets.Add(new ReviewTarget(path, passages));
        }
        return targets;
    }

    public static async Task<IReadOnlyList<ReviewTarget>> WholeAsync(
        RepositoryContext repository, IReadOnlyList<ReviewRule> rules, IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        var targets = new List<ReviewTarget>();
        foreach (var path in paths.Where(path => Reviewable(path, rules)))
        {
            var passages = Passages.Split(await File.ReadAllTextAsync(repository.Path(path), cancellationToken));
            if (passages.Count > 0) targets.Add(new ReviewTarget(path, passages));
        }
        return targets;
    }

    public static IReadOnlyList<ReviewRule> ChangedRules(IReadOnlyList<ReviewRule> rules, IEnumerable<string> paths)
    {
        var ids = paths.Select(ReviewRules.IdOf).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return rules.Where(rule => ids.Contains(rule.Id)).ToArray();
    }

    public static async Task<IReadOnlyList<string>> WorkspaceFilesAsync(IProcessRunner runner, CancellationToken cancellationToken)
    {
        var files = await runner.CaptureAsync("git",
            ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], null, cancellationToken);
        if (files.ExitCode != 0) throw new InvalidOperationException(files.StdErr.Trim());
        return files.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }
}
