using Spectre.Console;

namespace Orisumi.Dev;

internal sealed record LintFinding(string Rule, string Path, int Line, string Message);

internal sealed class Linter(RepositoryContext repository, IProcessRunner runner)
{
    public async Task<IReadOnlyList<LintFinding>> ScanAsync(IReadOnlyList<string>? changed, CancellationToken cancellationToken)
    {
        var files = await repository.Git.FilesAsync(cancellationToken);
        var findings = new List<LintFinding>();
        findings.AddRange(await new CommentLint(repository, runner).ScanAsync(changed ?? files, cancellationToken));
        if (changed is null || changed.Any(DocumentLint.Covers))
            findings.AddRange(DocumentLint.Scan(DocumentLint.Read(repository, files), Exists));
        findings.AddRange(await new UnusedLint(repository, runner, ProjectLayout.Load(repository.Root)).ScanAsync(changed, cancellationToken));
        return [.. findings.OrderBy(finding => finding.Path, StringComparer.Ordinal).ThenBy(finding => finding.Line)];
    }

    private bool Exists(string path) => File.Exists(repository.Path(path)) || Directory.Exists(repository.Path(path));

    internal static string Verdict(IReadOnlyList<LintFinding> findings) =>
        Terminal.Verdict(Outcome.Failed, $"{findings.Count} {(findings.Count == 1 ? "finding" : "findings")}");

    internal static void Write(IAnsiConsole console, IReadOnlyList<LintFinding> findings) =>
        Terminal.Rows(console, findings.Select(finding => new[]
        {
            $"  [yellow]{Terminal.Esc(finding.Rule)}[/]",
            Terminal.Esc($"{finding.Path}:{finding.Line}"),
            $"[grey]{Terminal.Esc(Terminal.Excerpt(finding.Message))}[/]",
        }));
}

internal sealed class LintCheck(RepositoryContext repository, IProcessRunner runner) : ICheck
{
    public string Name => "lint";

    public string Description => "comments, documents and unused code across the repository";

    public async Task<CheckOutcome> RunAsync(Run run, CancellationToken cancellationToken)
    {
        var findings = await new Linter(repository, runner).ScanAsync(null, cancellationToken);
        return findings.Count == 0
            ? new CheckOutcome(0)
            : new CheckOutcome(1, Linter.Verdict(findings), findings) { Details = console => Linter.Write(console, findings) };
    }
}
