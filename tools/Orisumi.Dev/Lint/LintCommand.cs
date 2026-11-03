using System.CommandLine;
using System.Text.Json;
using Spectre.Console;

namespace Orisumi.Dev;

internal sealed class LintCommand(RepositoryContext repository, IProcessRunner runner, TextWriter output)
{
    public Command CreateCommand()
    {
        var command = new Command("lint", "Check every source file for code comments; names and tests carry the explanation");
        var json = new Option<bool>("--json") { Description = "Print JSON" };
        command.Options.Add(json);
        command.SetAction((parse, token) => RunAsync(parse.GetValue(json), token));
        return command;
    }

    public async Task<int> RunAsync(bool json, CancellationToken cancellationToken)
    {
        var findings = await new CommentLint(repository, runner).ScanAsync(cancellationToken);
        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new { comments = findings }, JsonOptions.Indented));
            return findings.Count == 0 ? 0 : 1;
        }
        var console = Terminal.Create(output);
        if (findings.Count == 0)
        {
            console.MarkupLine($"{Terminal.Mark(Outcome.Passed)} No comments");
            return 0;
        }
        Terminal.Heading(console, "Lint", Terminal.Verdict(Outcome.Failed, $"{findings.Count} {(findings.Count == 1 ? "comment" : "comments")}"));
        Terminal.Rows(console, findings.Select(finding => new[]
        {
            Terminal.Esc($"{finding.Path}:{finding.Line}"),
            $"[grey]{Terminal.Esc(Reviewer.Excerpt(finding.Text))}[/]",
        }));
        console.WriteLine();
        console.MarkupLine("  [grey]a better name or a test replaces the comment[/]");
        return 1;
    }
}
