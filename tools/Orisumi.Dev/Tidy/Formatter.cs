using System.Text.Json;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace Orisumi.Dev;

internal sealed partial class Formatter(RepositoryContext repository, IProcessRunner runner)
{
    public static bool Covers(string path) => SourceLanguages.Of(path) is "C#" or "TypeScript" or "JavaScript";

    public Task<IReadOnlyList<string>> CheckAsync(IEnumerable<string> paths, CancellationToken cancellationToken) =>
        RunAsync(paths, write: false, cancellationToken);

    public Task<IReadOnlyList<string>> WriteAsync(IEnumerable<string> paths, CancellationToken cancellationToken) =>
        RunAsync(paths, write: true, cancellationToken);

    private async Task<IReadOnlyList<string>> RunAsync(IEnumerable<string> paths, bool write, CancellationToken cancellationToken)
    {
        var files = paths.Where(path => Covers(path) && !SourceLanguages.IsGenerated(path) && File.Exists(repository.Path(path))).ToArray();
        var csharp = files.Where(path => SourceLanguages.Of(path) == "C#").ToArray();
        var scripts = files.Except(csharp).ToArray();
        return [.. await CSharpAsync(csharp, write, cancellationToken), .. await ScriptsAsync(scripts, write, cancellationToken)];
    }

    private async Task<IEnumerable<string>> CSharpAsync(string[] files, bool write, CancellationToken cancellationToken)
    {
        if (files.Length == 0) return [];
        List<string> arguments = ["format", "whitespace", "--folder", "--include", .. files];
        if (!write) arguments.Add("--verify-no-changes");
        var result = await runner.CaptureAsync(new("dotnet", arguments, repository.Root,
            new Dictionary<string, string?> { ["DOTNET_CLI_UI_LANGUAGE"] = "en-US" }), cancellationToken);
        if (write)
        {
            if (result.ExitCode != 0) throw new InvalidOperationException($"dotnet format failed: {result.LastLine}");
            return [];
        }
        return Unformatted().Matches(result.Output)
            .Select(match => Path.GetRelativePath(repository.Root, match.Groups["path"].Value).Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal);
    }

    private async Task<IEnumerable<string>> ScriptsAsync(string[] files, bool write, CancellationToken cancellationToken)
    {
        if (files.Length == 0) return [];
        var tools = new NodeTools(repository, runner);
        await tools.EnsureAsync(cancellationToken);
        var list = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(list, JsonSerializer.Serialize(files), cancellationToken);
            var result = await runner.CaptureAsync(new("node", [tools.Script("format.mjs"), repository.Root, list, write ? "write" : "check"]), cancellationToken);
            if (result.ExitCode != 0) throw new InvalidOperationException($"Formatting scripts failed: {result.StdErr.Trim()}");
            var changed = JsonSerializer.Deserialize<string[]>(result.StdOut) ?? [];
            return write ? [] : changed;
        }
        finally
        {
            File.Delete(list);
        }
    }

    [GeneratedRegex(@"^(?<path>/[^(\r\n]+\.cs)\(\d+,\d+\): (?:error|warning) WHITESPACE", RegexOptions.Multiline)]
    private static partial Regex Unformatted();
}

internal sealed class FormatCheck(RepositoryContext repository, IProcessRunner runner, IReadOnlyList<string>? changed) : ICheck
{
    public string Name => "tidy";

    public string Description => changed is null ? "every code file is formatted" : "changed code files are formatted";

    public async Task<CheckOutcome> RunAsync(Run run, CancellationToken cancellationToken)
    {
        var files = changed ?? await repository.Git.FilesAsync(cancellationToken);
        var unformatted = await new Formatter(repository, runner).CheckAsync(files, cancellationToken);
        return unformatted.Count == 0
            ? new CheckOutcome(0)
            : new CheckOutcome(1, Terminal.Verdict(Outcome.Failed, $"{unformatted.Count} unformatted"), unformatted)
            {
                Details = console =>
                {
                    foreach (var file in unformatted) console.MarkupLine($"    [yellow]format[/] {Terminal.Esc(file)}");
                    console.MarkupLine("  [grey]orisumi tidy formats them[/]");
                },
            };
    }
}
