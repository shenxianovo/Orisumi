using System.CommandLine;
using System.Text.Json;

namespace Orisumi.Dev;

internal sealed class TidyCommand(RepositoryContext repository, IProcessRunner runner, TextWriter output)
{
    private sealed record UnusedRecord(string File, string Key);

    public Command CreateCommand()
    {
        var command = new Command("tidy", "Format the changed files, remove unused answer records and lint what changed");
        var json = CommandLine.Json();
        command.Options.Add(json);
        command.SetAction((parse, token) => RunAsync(parse.GetValue(json), token));
        return command;
    }

    private async Task<int> RunAsync(bool json, CancellationToken cancellationToken)
    {
        var changed = await repository.Git.ChangedPathsAsync("HEAD", cancellationToken);
        await new Formatter(repository, runner).WriteAsync(changed, cancellationToken);
        var findings = await new Linter(repository, runner).ScanAsync(changed, cancellationToken);
        if (json)
        {
            await CommandLine.WriteJsonAsync(output, new { changedPaths = changed, findings });
            return findings.Count == 0 ? 0 : 1;
        }
        var console = Terminal.Create(output);
        var summary = $"{changed.Count} changed {(changed.Count == 1 ? "file" : "files")}";
        Terminal.Heading(console, "Tidy", Terminal.Esc(summary), findings.Count == 0 ? Terminal.Verdict(Outcome.Passed, "clean") : Linter.Verdict(findings));
        Linter.Write(console, findings);
        return findings.Count == 0 ? 0 : 1;
    }
}
