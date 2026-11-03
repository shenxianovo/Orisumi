using System.Text.Json;
using Spectre.Console;

namespace Orisumi.Dev;

internal static class RunFiles
{
    public const string Manifest = "manifest.json";
    public const string Verification = "verification.json";
    public const string Quality = "quality.json";
    public const string Review = "review.json";
    public const string ReviewCalls = "review-calls.jsonl";
    public const string NoiseCalls = "noise-calls.jsonl";
}

internal sealed record RunManifest(
    string RunId,
    string Title,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    int? ExitCode,
    IReadOnlyList<string> Commands,
    IReadOnlyList<string> Artifacts,
    string? Failure);

internal sealed class Run(string id, string directory, string title, DateTimeOffset startedAt)
{
    public string Id { get; } = id;
    public string Directory { get; } = directory;
    public string Title { get; } = title;
    public List<string> Commands { get; } = [];

    public string PathOf(string file) => Path.Combine(Directory, file);

    public string Log(string name) =>
        PathOf(string.Concat(name.Select(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '-')) + ".log");

    public Task WriteJsonAsync(string file, object value) =>
        File.WriteAllTextAsync(PathOf(file), JsonSerializer.Serialize(value, JsonOptions.Indented) + Environment.NewLine, CancellationToken.None);

    public static async Task<int> ExecuteAsync(
        RepositoryContext repository, string title, Func<Run, Task<int>> action, TextWriter? notes = null)
    {
        var store = new RunStore(repository);
        var run = store.Create(title);
        await run.WriteManifestAsync(null, null);
        int? exitCode = null;
        string? failure = null;
        try
        {
            exitCode = await action(run);
            return exitCode.Value;
        }
        catch (OperationCanceledException)
        {
            exitCode = 130;
            failure = nameof(OperationCanceledException);
            throw;
        }
        catch (Exception exception)
        {
            exitCode = 1;
            failure = exception.GetType().Name;
            throw;
        }
        finally
        {
            await run.WriteManifestAsync(exitCode, failure);
            Collect(store, notes);
        }
    }

    private Task WriteManifestAsync(int? exitCode, string? failure)
    {
        var artifacts = System.IO.Directory.EnumerateFileSystemEntries(Directory)
            .Select(entry => Path.GetFileName(entry))
            .Where(name => name != RunFiles.Manifest)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return WriteJsonAsync(RunFiles.Manifest, new RunManifest(
            Id, Title, startedAt, exitCode is null ? null : DateTimeOffset.UtcNow, exitCode, Commands, artifacts, failure));
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
