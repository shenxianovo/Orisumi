using System.CommandLine;
using System.Globalization;
using Spectre.Console;

namespace Orisumi.Dev;

internal sealed class ReviewCommand(RepositoryContext repository, IProcessRunner runner, ModelAccess models, TextWriter output)
{
    public Command CreateCommand()
    {
        var command = new Command("review", "Report what changed since a Git base: complexity, writing and declared exceptions");
        var baseRef = new Option<string?>("--base") { Description = "Git comparison base; HEAD when the worktree has changes" };
        var json = CommandLine.Json();
        command.Options.Add(baseRef);
        command.Options.Add(json);
        command.SetAction((parse, token) => RunAsync(parse.GetValue(baseRef), parse.GetValue(json), token));
        return command;
    }

    private async Task<int> RunAsync(string? baseRef, bool json, CancellationToken token)
    {
        var requested = await repository.Git.BaseAsync(baseRef, token);
        var commit = await repository.Git.CommitAsync(requested, token);
        return await Run.ExecuteAsync(repository, "review", async run =>
        {
            var console = Terminal.Create(output);
            var reviewer = new Reviewer(repository, runner, models);
            var report = json
                ? await reviewer.ReviewAsync(requested, commit, run, token)
                : await Terminal.SpinAsync(console, $"Reviewing changes since {Terminal.Esc(requested)}", () => reviewer.ReviewAsync(requested, commit, run, token));
            if (json) await CommandLine.WriteJsonAsync(output, new { evidence = run.Directory, review = report });
            else Write(console, report, run);
            return report.Completed ? 0 : 1;
        }, notes: json ? null : output);
    }

    internal static string Verdict(ReviewReport report) =>
        !report.Completed ? Terminal.Verdict(Outcome.Failed, "incomplete")
        : report.Findings > 0 ? Terminal.Verdict(Outcome.Attention, $"{report.Findings} {(report.Findings == 1 ? "finding" : "findings")}")
        : Terminal.Verdict(Outcome.Passed, "nothing new");

    private void Write(IAnsiConsole console, ReviewReport report, Run run)
    {
        Terminal.Heading(console, "Review", Terminal.Esc($"against {report.Base} ({Terminal.ShortRevision(report.Commit)})"), Verdict(report));
        var rows = Rows(report);
        if (rows.Count > 1) Terminal.Rows(console, rows, 1, 2);
        var findings = Findings(report).ToArray();
        if (findings.Length > 0) console.WriteLine();
        foreach (var (kind, text, note) in findings)
            console.MarkupLine($"    [yellow]{kind,-9}[/] {Terminal.Esc(text)}" + (note.Length > 0 ? $"  [grey]{Terminal.Esc(note)}[/]" : ""));
        if (!report.Judged) console.MarkupLine("    [grey]writing, noise and exceptions not judged: no OpenAI API key[/]");
        if (report.Failures.Count > 0) console.WriteLine();
        foreach (var failure in report.Failures) console.MarkupLine($"    {Terminal.Mark(Outcome.Failed)} {Terminal.Esc(failure)}");
        console.WriteLine();
        console.MarkupLine($"  [grey]evidence[/]  {Terminal.Esc(Terminal.Path(repository, run.PathOf(RunFiles.Review)))}");
    }

    private static List<string[]> Rows(ReviewReport report)
    {
        var complexity = report.Complexity;
        List<string[]> rows = [["", "[grey]current[/]", "[grey]change[/]"]];
        if (complexity.ProductionLines is { } lines) rows.Add(["Production LOC", Terminal.Number(lines.Current), Terminal.Delta(lines.Delta)]);
        if (complexity.Erosion is not { } erosion) return rows;
        rows.Add([$"Complex functions > {QualityAnalysis.Threshold}", Terminal.Number(complexity.Hotspots.Count),
            complexity.Regressions is null ? "[grey]n/a[/]" : Terminal.Number(report.NewComplexity.Count())]);
        rows.Add(["Complexity burden", erosion.Ratio.ToString("P1", CultureInfo.InvariantCulture),
            complexity.ErosionDelta is { } delta ? (delta * 100).ToString("+0.0;-0.0;±0.0", CultureInfo.InvariantCulture) + "pp" : "[grey]n/a[/]"]);
        return rows;
    }

    private static IEnumerable<(string Kind, string Text, string Note)> Findings(ReviewReport report) =>
        report.Regressions.Select(review => (
                review.Noise is null ? "complex" : "noise",
                $"{review.Regression.Current.Path}:{review.Regression.Current.Line} {QualityAnalysis.ShortSymbol(review.Regression.Current.Symbol)} · "
                    + $"{review.Regression.Current.Complexity}" + (review.Regression.BaseComplexity is { } before ? $" (was {before})" : " (new)"),
                review.Noise is null ? "" : $"{review.Noise} {review.Probability:0.00}"))
            .Concat(report.Writing.Select(finding => ("writing", $"{finding.Path}:{finding.Line} {finding.Title}", $"{finding.Probability:0.00} {finding.Excerpt}")))
            .Concat(report.BrokenExceptions.Select(review => ("exception",
                $"{review.Exception.Path} {review.Exception.Symbol} · {review.Exception.Rule}",
                $"{review.Exception.Reason} no longer holds ({review.Probability:0.00})")));
}
