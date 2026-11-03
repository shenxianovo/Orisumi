using System.CommandLine;
using Spectre.Console;

namespace Orisumi.Dev;

internal sealed class LintCommand(RepositoryContext repository, IProcessRunner runner, TextWriter output)
{
    public Command CreateCommand()
    {
        var command = new Command("lint", "Check every source file for code comments; names and tests carry the explanation");
        var json = CommandLine.Json();
        command.Options.Add(json);
        command.SetAction((parse, token) => RunAsync(parse.GetValue(json), token));
        return command;
    }

    private async Task<int> RunAsync(bool json, CancellationToken cancellationToken)
    {
        var findings = await new CommentLint(repository, runner).ScanAsync(cancellationToken);
        if (json)
        {
            await CommandLine.WriteJsonAsync(output, new { comments = findings });
            return findings.Count == 0 ? 0 : 1;
        }
        var console = Terminal.Create(output);
        if (findings.Count == 0)
        {
            console.MarkupLine($"{Terminal.Mark(Outcome.Passed)} No comments");
            return 0;
        }
        Terminal.Heading(console, "Lint", LintCheck.Verdict(findings));
        LintCheck.WriteFindings(console, findings);
        return 1;
    }
}

internal sealed class LintCheck(RepositoryContext repository, IProcessRunner runner) : ICheck
{
    public string Name => "lint";

    public string Description => "orisumi lint";

    public async Task<CheckOutcome> RunAsync(Run run, CancellationToken cancellationToken)
    {
        var findings = await new CommentLint(repository, runner).ScanAsync(cancellationToken);
        return findings.Count == 0
            ? new CheckOutcome(0)
            : new CheckOutcome(1, Verdict(findings), findings) { Details = console => WriteFindings(console, findings) };
    }

    internal static string Verdict(IReadOnlyList<CommentFinding> findings) =>
        Terminal.Verdict(Outcome.Failed, $"{findings.Count} {(findings.Count == 1 ? "comment" : "comments")}");

    internal static void WriteFindings(IAnsiConsole console, IReadOnlyList<CommentFinding> findings)
    {
        Terminal.Rows(console, findings.Select(finding => new[]
        {
            Terminal.Esc($"{finding.Path}:{finding.Line}"),
            $"[grey]{Terminal.Esc(Terminal.Excerpt(finding.Text))}[/]",
        }));
        console.MarkupLine("  [grey]a better name or a test replaces the comment[/]");
    }
}
