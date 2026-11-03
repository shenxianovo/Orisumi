using System.CommandLine;
using System.Globalization;
using Spectre.Console;

namespace Orisumi.Dev;

internal sealed class QualityCommand(RepositoryContext repository, IProcessRunner runner, ModelAccess models, TextWriter output)
{
    public Command CreateCommand()
    {
        var command = new Command("quality", "Observe structural quality relative to a Git base");
        var baseRef = new Option<string?>("--base") { Description = "Git comparison base; HEAD when the worktree has changes" };
        var noise = new Option<bool>("--noise") { Description = "Also list findings folded as declared noise" };
        var json = CommandLine.Json();
        command.Options.Add(baseRef);
        command.Options.Add(noise);
        command.Options.Add(json);
        command.SetAction((parse, token) => RunAsync(parse.GetValue(baseRef), parse.GetValue(noise), parse.GetValue(json), token));
        return command;
    }

    private async Task<int> RunAsync(string? baseRef, bool noise, bool json, CancellationToken token)
    {
        var requested = await repository.Git.BaseAsync(baseRef, token);
        var check = new QualityCheck(repository, runner, models, requested, await repository.Git.CommitAsync(requested, token));
        return await Run.ExecuteAsync(repository, "analyze quality", async run =>
        {
            var console = Terminal.Create(output);
            var outcome = json
                ? await check.RunAsync(run, token)
                : await Terminal.SpinAsync(console, $"Analyzing quality against {Terminal.Esc(requested)}", () => check.RunAsync(run, token));
            var report = (QualityReport)outcome.Report!;
            if (json) await CommandLine.WriteJsonAsync(output, report);
            else QualityCheck.Write(console, repository, report, run, noise);
            return outcome.ExitCode;
        }, notes: json ? null : output);
    }
}

internal sealed class QualityCheck(RepositoryContext repository, IProcessRunner runner, ModelAccess models, string requested, string commit) : ICheck
{
    public string Name => "quality";

    public string Description => $"structural analysis against {requested} ({Terminal.ShortRevision(commit)}), noise judged by the model";

    public async Task<CheckOutcome> RunAsync(Run run, CancellationToken cancellationToken)
    {
        var report = await new QualityAnalysis(repository, runner, models).AnalyzeAsync(requested, commit, run, cancellationToken);
        return new CheckOutcome(report.Completed ? 0 : 1, Verdict(report), report) { Details = console => WriteFindings(console, report) };
    }

    internal static string Verdict(QualityReport report) => !report.Completed
        ? Terminal.Verdict(Outcome.Failed, "incomplete")
        : report.NewFindings is var found and > 0
            ? Terminal.Verdict(Outcome.Attention, $"{found} new {(found == 1 ? "finding" : "findings")}")
            : Terminal.Verdict(Outcome.Passed, "nothing new");

    internal static void Write(IAnsiConsole console, RepositoryContext repository, QualityReport report, Run run, bool noise)
    {
        Terminal.Heading(console, "Quality", Terminal.Esc($"against {report.Base} ({Terminal.ShortRevision(report.Commit)})"), Verdict(report));
        var rows = Rows(report);
        if (rows.Count > 1) Terminal.Rows(console, rows, 1, 2);
        WriteFindings(console, report);
        if (noise && report.Noise is { } judged) WriteDeclared(console, judged);
        console.WriteLine();
        console.MarkupLine($"  [grey]evidence[/]  {Terminal.Esc(Terminal.Path(repository, run.PathOf(RunFiles.Quality)))}");
    }

    private static List<string[]> Rows(QualityReport report)
    {
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
            rows.Add([$"Complex functions > {QualityAnalysis.Threshold}", Terminal.Number(report.Hotspots.Count()), New(report.Regressions?.Count())]);
            rows.Add(["Complexity burden", complexity.Erosion.Ratio.ToString("P1", CultureInfo.InvariantCulture),
                complexity.ErosionDelta is { } delta ? (delta * 100).ToString("+0.0;-0.0;±0.0", CultureInfo.InvariantCulture) + "pp" : "[grey]n/a[/]"]);
        }
        if (report.Unused is not null) rows.Add(["Unused code", Terminal.Number(report.UnusedCode.Count()), New(report.NewUnusedCode?.Count())]);
        if (report.Noise?.With(NoiseStatus.Declared).Count() is > 0 and var folded)
            rows.Add(["Declared noise", $"[grey]{Terminal.Number(folded)}[/]", ""]);
        return rows;
    }

    internal static void WriteFindings(IAnsiConsole console, QualityReport report)
    {
        var lines = FindingLines(report).ToArray();
        if (lines.Length > 0) console.WriteLine();
        foreach (var (kind, text, note) in lines)
            console.MarkupLine($"    [yellow]{kind,-8}[/] {Terminal.Esc(text)}" + (note.Length > 0 ? $"  [grey]{Terminal.Esc(note)}[/]" : ""));
        if (report.Noise is { Judged: false }) console.MarkupLine("    [grey]noise not judged: no OpenAI API key[/]");
        if (report.Noise?.With(NoiseStatus.Explained).Any() == true)
            console.MarkupLine($"    [grey]confirmed noise goes into the project's {NoiseJudge.FileName}; see the Orisumi.Dev README[/]");
        if (report.Failures.Count > 0) console.WriteLine();
        foreach (var failure in report.Failures) console.MarkupLine($"    {Terminal.Mark(Outcome.Failed)} {Terminal.Esc(failure)}");
    }

    private static IEnumerable<(string Kind, string Text, string Note)> FindingLines(QualityReport report)
    {
        string Note(QualityFinding finding) => report.Noise?.Of(finding) switch
        {
            { Status: NoiseStatus.Explained } judgment => $"likely noise: {judgment.ReasonTitle} {judgment.Probability:0.00}",
            { Status: NoiseStatus.Contradicted } judgment => $"declared {judgment.Declared}, which no longer holds",
            _ => "",
        };
        var regressions = (report.Regressions ?? []).ToArray();
        var newUnused = (report.NewUnusedCode ?? []).ToArray();
        var listed = regressions.Select(item => QualityFinding.Of(item.Current)).Concat(newUnused.Select(QualityFinding.Of)).ToHashSet();
        return regressions
            .Select(regression => ("complex",
                $"{regression.Current.Path}:{regression.Current.Line} {QualityFinding.ShortSymbol(regression.Current.Symbol)} · "
                + $"{regression.Current.Complexity}" + (regression.BaseComplexity is { } before ? $" (was {before})" : " (new)"),
                Note(QualityFinding.Of(regression.Current))))
            .Concat(newUnused.Select(finding => ("unused", $"{finding.Path}:{finding.Line} {finding.Symbol} · {finding.Rule}", Note(QualityFinding.Of(finding)))))
            .Concat((report.Noise?.Judgments ?? [])
                .Where(judgment => judgment.Status is NoiseStatus.Explained or NoiseStatus.Contradicted && !listed.Contains(judgment.Finding))
                .Select(judgment => ("noise?", Describe(judgment.Finding), Note(judgment.Finding))));
    }

    private static void WriteDeclared(IAnsiConsole console, NoiseReport noise)
    {
        var declared = noise.With(NoiseStatus.Declared).ToArray();
        if (declared.Length == 0) return;
        console.WriteLine();
        foreach (var judgment in declared)
            console.MarkupLine($"    [grey]noise    {Terminal.Esc(Describe(judgment.Finding))}  declared {Terminal.Esc(judgment.Declared!)}[/]");
    }

    private static string Describe(QualityFinding finding) => $"{finding.Path}:{finding.Line} {finding.Symbol} · {finding.Rule}";
}
