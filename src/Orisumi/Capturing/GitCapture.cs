using System.Text.Json;

namespace Orisumi.Capturing;

internal sealed record CommitRecord(string Sha, string Repository, string? Remote, string Commit, string Numstat);

internal static class GitCapture
{
    public static async Task<Capture> RunAsync(Store store, string codeRoot, TimeRange range)
    {
        var root = Path.GetFullPath(codeRoot);
        var writer = store.BeginCapture("git", root, range, (await Shell.CaptureAsync("git", ["--version"])).Trim());
        foreach (var repository in Repositories(root))
        {
            var log = await Shell.CaptureAsync("git", ["-C", repository, "log", "--all",
                $"--since={Clock.Iso(range.From)}", $"--until={Clock.Iso(range.To)}", "--format=%H"]);
            var shas = log.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (shas.Length == 0) continue;
            var relative = Path.GetRelativePath(root, repository);
            var remote = await RemoteAsync(repository);
            await using var output = new StreamWriter(writer.Target($"repos/{relative}.jsonl", repository));
            foreach (var sha in shas)
            {
                var commit = await Shell.CaptureAsync("git", ["-C", repository, "cat-file", "commit", sha]);
                var numstat = await Shell.CaptureAsync("git", ["-C", repository, "show", "--numstat", "--format=", sha]);
                await output.WriteLineAsync(JsonSerializer.Serialize(new CommitRecord(sha, relative, remote, commit, numstat), Store.Json));
            }
        }
        return writer.Complete();
    }

    private static IEnumerable<string> Repositories(string root) =>
        Directory.EnumerateDirectories(root, ".git", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = 3,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
        }).Select(Path.GetDirectoryName).OfType<string>().Order(StringComparer.Ordinal);

    private static async Task<string?> RemoteAsync(string repository)
    {
        try
        {
            return (await Shell.CaptureAsync("git", ["-C", repository, "remote", "get-url", "origin"])).Trim();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
