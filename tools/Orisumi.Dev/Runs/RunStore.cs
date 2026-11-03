using System.Text.Json;

namespace Orisumi.Dev;

internal sealed record RunRecord(string Id, string Directory, DateTimeOffset CreatedAt);

internal sealed record RunSummary(RunRecord Run, string Title, int? ExitCode, TimeSpan? Duration, IReadOnlyList<string> Failed);

internal sealed record RetentionPolicy(int Keep, int KeepFailed, TimeSpan OlderThan)
{
    public static readonly RetentionPolicy Default = new(10, 5, TimeSpan.FromDays(2));
}

internal sealed record PruneOutcome(IReadOnlyList<RunRecord> Candidates, long Bytes);

internal sealed class RunStore(RepositoryContext repository)
{
    public string Root { get; } = repository.Path(".artifacts", "runs");

    public Run Create(string title)
    {
        var now = DateTimeOffset.UtcNow;
        var slug = string.Concat(title.Select(character => char.IsLetterOrDigit(character) ? character : '-'));
        var id = $"{now:yyyyMMddTHHmmssZ}-{slug}-{Guid.NewGuid():N}";
        var directory = Path.Combine(Root, id);
        Directory.CreateDirectory(directory);
        return new Run(id, directory, title, now);
    }

    public IReadOnlyList<RunRecord> List() =>
        !Directory.Exists(Root) ? [] : [.. Directory.EnumerateDirectories(Root)
            .Select(path => new DirectoryInfo(path))
            .Select(info => new RunRecord(info.Name, info.FullName, info.CreationTimeUtc))
            .OrderByDescending(run => run.CreatedAt)];

    public PruneOutcome SelectForPruning(RetentionPolicy policy)
    {
        var runs = List();
        var retained = runs.Take(policy.Keep).Concat(runs.Where(IsFailed).Take(policy.KeepFailed))
            .Select(run => run.Id).ToHashSet(StringComparer.Ordinal);
        var cutoff = DateTimeOffset.UtcNow - policy.OlderThan;
        var candidates = runs.Where(run => !retained.Contains(run.Id) && run.CreatedAt < cutoff).ToArray();
        return new PruneOutcome(candidates, candidates.Sum(Bytes));
    }

    public PruneOutcome Prune(RetentionPolicy policy)
    {
        var outcome = SelectForPruning(policy);
        foreach (var candidate in outcome.Candidates) Directory.Delete(candidate.Directory, recursive: true);
        return outcome;
    }

    public static RunSummary Summarize(RunRecord run)
    {
        var manifest = Read<RunManifest>(run, RunFiles.Manifest);
        var verification = Read<VerificationRecord>(run, RunFiles.Verification);
        return new RunSummary(run, manifest?.Title ?? run.Id, manifest?.ExitCode,
            manifest?.CompletedAt - manifest?.StartedAt,
            [.. (verification?.Checks ?? []).Where(check => check.ExitCode is not (0 or 130)).Select(check => check.Name)]);
    }

    private static bool IsFailed(RunRecord run) => Read<RunManifest>(run, RunFiles.Manifest)?.ExitCode is not 0;

    private static T? Read<T>(RunRecord run, string file) where T : class
    {
        var path = Path.Combine(run.Directory, file);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions.Indented) : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static long Bytes(RunRecord run)
    {
        try
        {
            return new DirectoryInfo(run.Directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes;
        var suffix = 0;
        while (value >= 1024 && suffix < suffixes.Length - 1)
        {
            value /= 1024;
            suffix++;
        }
        return $"{value:0.##} {suffixes[suffix]}";
    }
}
