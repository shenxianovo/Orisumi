using Spectre.Console;

namespace Orisumi.Dev;

internal sealed class EvidenceSession(RunRecord run)
{
    public RunRecord Run { get; } = run;
    public List<string> Commands { get; } = [];

    public static async Task<int> ExecuteAsync(
        RepositoryContext repository,
        string kind,
        string name,
        IReadOnlyList<string> limitations,
        Func<EvidenceSession, Task<int>> action,
        TextWriter? notes = null)
    {
        var store = new RunStore(repository);
        var session = new EvidenceSession(store.Create(kind, name));
        var exitCode = 1;
        string? failure = null;
        try
        {
            exitCode = await action(session);
            return exitCode;
        }
        catch (OperationCanceledException)
        {
            exitCode = 130;
            failure = "OperationCanceledException";
            throw;
        }
        catch (Exception exception)
        {
            failure = exception.GetType().Name;
            throw;
        }
        finally
        {
            var finalLimitations = limitations.ToList();
            var artifacts = Directory.EnumerateFileSystemEntries(session.Run.Directory)
                .Select(Path.GetFileName)
                .Where(item => item is not null and not "manifest.json")
                .Select(item => item!)
                .Order(StringComparer.Ordinal)
                .ToArray();
            await RunStore.WriteManifestAsync(session.Run, new EvidenceManifest(
                session.Run.Id, kind, name, session.Run.CreatedAt, DateTimeOffset.UtcNow,
                exitCode, session.Commands, artifacts, finalLimitations, failure),
                CancellationToken.None);
            Collect(store, notes);
        }
    }

    private static void Collect(RunStore store, TextWriter? notes)
    {
        try
        {
            var pruned = store.Prune(RetentionPolicy.Default);
            if (pruned.Candidates.Count == 0 || notes is null) return;
            Terminal.Create(notes).MarkupLine(
                $"[grey]pruned {pruned.Candidates.Count} old runs ({Terminal.Esc(RunStore.FormatBytes(pruned.Bytes))}); see orisumi runs prune[/]");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (notes is not null)
                Terminal.Create(notes).MarkupLine($"{Terminal.Mark(Outcome.Attention)} Could not prune old runs: {Terminal.Esc(exception.Message)}");
        }
    }
}
