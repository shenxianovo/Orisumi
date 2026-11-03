using System.CommandLine;
using System.Globalization;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace Orisumi.Dev;

internal sealed class QualityCommand(RepositoryContext repository, IProcessRunner runner, TextWriter output)
{
    public Command CreateCommand()
    {
        var command = new Command("quality", "Observe structural quality relative to a Git base");
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
        var check = new QualityCheck(repository, runner, requested, await repository.Git.CommitAsync(requested, token));
        return await Run.ExecuteAsync(repository, "analyze quality", async run =>
        {
            var console = Terminal.Create(output);
            var outcome = json
                ? await check.RunAsync(run, token)
                : await Terminal.SpinAsync(console, $"Analyzing quality against {Terminal.Esc(requested)}", () => check.RunAsync(run, token));
            var report = (QualityReport)outcome.Report!;
            if (json) await CommandLine.WriteJsonAsync(output, report);
            else QualityCheck.Write(console, repository, report, run);
            return outcome.ExitCode;
        }, notes: json ? null : output);
    }
}

internal sealed partial class QualityCheck(RepositoryContext repository, IProcessRunner runner, string requested, string commit) : ICheck
{
    public string Name => "quality";

    public string Description => $"structural analysis against {requested} ({Terminal.ShortRevision(commit)})";

    public async Task<CheckOutcome> RunAsync(Run run, CancellationToken cancellationToken)
    {
        var report = await new QualityAnalysis(repository, runner).AnalyzeAsync(requested, commit, run, cancellationToken);
        return new CheckOutcome(report.Completed ? 0 : 1, Verdict(report), report) { Details = console => WriteFindings(console, report) };
    }

    internal static string Verdict(QualityReport report) => !report.Completed
        ? Terminal.Verdict(Outcome.Failed, "incomplete")
        : report.NewFindings is var found and > 0
            ? Terminal.Verdict(Outcome.Attention, $"{found} new {(found == 1 ? "finding" : "findings")}")
            : Terminal.Verdict(Outcome.Passed, "nothing new");

    internal static void Write(IAnsiConsole console, RepositoryContext repository, QualityReport report, Run run)
    {
        Terminal.Heading(console, "Quality", Terminal.Esc($"against {report.Base} ({Terminal.ShortRevision(report.Commit)})"), Verdict(report));
        static string New(int? count) => count switch
        {
            null => "[grey]n/a[/]",
            > 0 => $"[yellow]{Terminal.Number(count.Value)}[/]",
            _ => "0",
        };
        List<string[]> rows = [["", "[grey]current[/]", "[grey]new[/]"]];
        if (report.ProductionLines is { } lines)
            rows.Add(["Production LOC", Terminal.Number(lines.Current), Terminal.Delta(lines.Delta)]);
        if (report.Complexity is { } complexity)
        {
            rows.Add([$"Complex functions > {QualityAnalysis.Threshold}", Terminal.Number(complexity.Hotspots.Count), New(complexity.Regressions?.Count)]);
            rows.Add(["Complexity burden", complexity.Erosion.Ratio.ToString("P1", CultureInfo.InvariantCulture),
                complexity.ErosionDelta is { } delta ? (delta * 100).ToString("+0.0;-0.0;±0.0", CultureInfo.InvariantCulture) + "pp" : "[grey]n/a[/]"]);
        }
        if (report.Unused is { } unused) rows.Add(["Unused code", Terminal.Number(unused.Current.Count), New(unused.New?.Count)]);
        if (rows.Count > 1) Terminal.Rows(console, rows, 1, 2);
        WriteFindings(console, report);
        console.WriteLine();
        console.MarkupLine($"  [grey]evidence[/]  {Terminal.Esc(Terminal.Path(repository, run.PathOf(RunFiles.Quality)))}");
    }

    internal static void WriteFindings(IAnsiConsole console, QualityReport report)
    {
        var findings = NewFindingLines(report).ToArray();
        if (findings.Length > 0) console.WriteLine();
        foreach (var (kind, text) in findings) console.MarkupLine($"    [yellow]{kind,-8}[/] {Terminal.Esc(text)}");
        if (report.Failures.Count > 0) console.WriteLine();
        foreach (var failure in report.Failures) console.MarkupLine($"    {Terminal.Mark(Outcome.Failed)} {Terminal.Esc(failure)}");
    }

    private static IEnumerable<(string Kind, string Text)> NewFindingLines(QualityReport report) =>
        (report.Complexity?.Regressions ?? [])
            .Select(regression => ("complex",
                $"{regression.Current.Path}:{regression.Current.Line} {ShortSymbol(regression.Current.Symbol)} · "
                + $"{regression.Current.Complexity}" + (regression.BaseComplexity is { } before ? $" (was {before})" : " (new)")))
            .Concat((report.Unused?.New ?? []).Select(finding => ("unused", $"{finding.Path}:{finding.Line} {finding.Symbol} · {finding.Rule}")));

    internal static string ShortSymbol(string symbol)
    {
        var segments = Parameters().Replace(symbol, "").Split('/')
            .Select(segment => Arity().Replace(Kind().Replace(segment, ""), ""))
            .Where(segment => segment.Length > 0)
            .ToArray();
        return string.Join('.', segments.TakeLast(2));
    }

    [GeneratedRegex(@"\(.*\)")]
    private static partial Regex Parameters();

    [GeneratedRegex(@"^[A-Za-z]+:")]
    private static partial Regex Kind();

    [GeneratedRegex(@"`\d+")]
    private static partial Regex Arity();
}
