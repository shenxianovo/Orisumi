using System.CommandLine;
using System.Globalization;
using Spectre.Console;

namespace Orisumi.Dev;

internal sealed class ReviewCommand(RepositoryContext repository, ModelAccess models, TextWriter output)
{
    public Command CreateCommand()
    {
        var check = new Command("check", "Review changed passages by default, or the given files, or every covered file");
        var paths = new Argument<string[]>("paths") { Arity = ArgumentArity.ZeroOrMore, Description = "Files to review in full" };
        var baseRef = new Option<string?>("--base") { Description = "Git comparison base for changed passages" };
        var all = new Option<bool>("--all") { Description = "Review every file a rule covers" };
        var checkJson = CommandLine.Json();
        check.Arguments.Add(paths);
        check.Options.Add(baseRef);
        check.Options.Add(all);
        check.Options.Add(checkJson);
        check.SetAction((parse, token) => CheckAsync(
            parse.GetValue(paths) ?? [], parse.GetValue(baseRef), parse.GetValue(all), parse.GetValue(checkJson), token));

        var rules = new Command("rules", "List the rules and the files each one covers");
        var rulesJson = CommandLine.Json();
        rules.Options.Add(rulesJson);
        rules.SetAction(parse => RulesAsync(parse.GetValue(rulesJson)));

        var calibrate = new Command("calibrate", "Judge each rule's own examples to show it catches what it should");
        var ids = new Argument<string[]>("rules") { Arity = ArgumentArity.ZeroOrMore, Description = "Rule ids; all rules when omitted" };
        var calibrateJson = CommandLine.Json();
        calibrate.Arguments.Add(ids);
        calibrate.Options.Add(calibrateJson);
        calibrate.SetAction((parse, token) => CalibrateAsync(parse.GetValue(ids) ?? [], parse.GetValue(calibrateJson), token));

        return CommandLine.Group("review", "Judge writing against rules that need a model's judgment", check, rules, calibrate);
    }

    private async Task<int> CheckAsync(string[] paths, string? baseRef, bool all, bool json, CancellationToken token)
    {
        if ((paths.Length > 0 ? 1 : 0) + (all ? 1 : 0) + (baseRef is not null ? 1 : 0) > 1)
            throw new CommandUsageException("Choose one of: paths, --all, --base.");
        var rules = ReviewRules.Load(repository);
        if (all)
            return await RunAsync("review check", "every covered file",
                await ReviewCheck.WholeAsync(repository, models, rules, await repository.Git.FilesAsync(token), token), json, token);
        if (paths.Length > 0)
        {
            var relative = paths.Select(RepositoryPath).ToArray();
            return await RunAsync("review check", $"{relative.Length} {(relative.Length == 1 ? "file" : "files")}",
                await ReviewCheck.WholeAsync(repository, models, rules, relative, token), json, token);
        }
        var resolved = await repository.Git.BaseAsync(baseRef, token);
        var changed = await repository.Git.ChangedPathsAsync(resolved, token);
        return await RunAsync("review check", $"changed since {resolved}",
            await ReviewCheck.ForChangesAsync(repository, models, resolved, changed, token), json, token);
    }

    private string RepositoryPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new CommandUsageException($"No such file: {path}");
        var relative = Path.GetRelativePath(repository.Root, full).Replace('\\', '/');
        if (relative.StartsWith("..", StringComparison.Ordinal)) throw new CommandUsageException($"{path} is outside this checkout.");
        return relative;
    }

    private async Task<int> CalibrateAsync(string[] ids, bool json, CancellationToken token)
    {
        var rules = ReviewRules.Load(repository);
        var unknown = ids.Where(id => rules.All(rule => rule.Id != id)).ToArray();
        if (unknown.Length > 0)
            throw new CommandUsageException($"Unknown rule {string.Join(", ", unknown)}. Rules: {string.Join(", ", rules.Select(rule => rule.Id))}.");
        var selected = ids.Length == 0 ? rules : [.. rules.Where(rule => ids.Contains(rule.Id))];
        return await RunAsync("review calibrate", "calibration",
            selected.Count == 0 ? null : new ReviewCheck(repository, models, rules, [], selected), json, token);
    }

    private async Task<int> RunAsync(string title, string scope, ReviewCheck? check, bool json, CancellationToken token)
    {
        var console = Terminal.Create(output);
        if (check is null)
        {
            if (json) await CommandLine.WriteJsonAsync(output, new { evidence = (string?)null, review = (ReviewReport?)null });
            else console.MarkupLine($"{Terminal.Mark(Outcome.Passed)} Nothing to review{Terminal.Dot}[grey]{Terminal.Esc(scope)} has no passages a rule covers[/]");
            return 0;
        }
        if (models.Client() is null)
        {
            Terminal.Error(output, "No OpenAI API key");
            Terminal.Hint(console, ModelAccess.SetupHint);
            return 1;
        }
        return await Run.ExecuteAsync(repository, title, async run =>
        {
            var outcome = json
                ? await check.RunAsync(run, token)
                : await Terminal.SpinAsync(console, "Reviewing", () => check.RunAsync(run, token));
            var report = (ReviewReport)outcome.Report!;
            if (json)
                await CommandLine.WriteJsonAsync(output, new { evidence = run.Directory, review = report });
            else
                Write(console, scope, report, run);
            return outcome.ExitCode;
        }, notes: json ? null : output);
    }

    private async Task<int> RulesAsync(bool json)
    {
        var rules = ReviewRules.Load(repository);
        if (json)
        {
            await CommandLine.WriteJsonAsync(output, new { rules });
            return 0;
        }
        var console = Terminal.Create(output);
        Terminal.Heading(console, "Review rules", $"{rules.Count} {(rules.Count == 1 ? "rule" : "rules")}");
        Terminal.Rows(console, rules.Select(rule => new[]
        {
            Terminal.Esc(rule.Id),
            $"[grey]{rule.Kind.ToString().ToLowerInvariant()}[/]",
            Terminal.Esc(rule.Title),
            $"[grey]{Terminal.Esc(string.Join(", ", rule.Covers))}[/]",
            $"[grey]{rule.Yes.Count} yes · {rule.No.Count} no examples[/]",
        }));
        return 0;
    }

    private void Write(IAnsiConsole console, string scope, ReviewReport report, Run run)
    {
        Terminal.Heading(console, "Review", Terminal.Esc(scope), ReviewCheck.Verdict(report));
        var cached = report.Cached > 0 ? $", {report.Cached} cached" : "";
        var refused = report.Refused > 0 ? $", {report.Refused} refused" : "";
        console.MarkupLine($"  [grey]{Count(report.Files.Count, "file")} · {Count(report.Calibrated.Count, "rule")} calibrated · "
            + $"{Count(report.Questions, "question")}{cached}{refused} · {Terminal.Esc(report.Model)}[/]");
        ReviewCheck.WriteFindings(console, report);
        console.WriteLine();
        console.MarkupLine($"  [grey]evidence[/]  {Terminal.Esc(Terminal.Path(repository, run.PathOf(RunFiles.Review)))}");
    }

    private static string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";
}

internal sealed class ReviewCheck(
    RepositoryContext repository,
    ModelAccess models,
    IReadOnlyList<ReviewRule> rules,
    IReadOnlyList<ReviewTarget> targets,
    IReadOnlyList<ReviewRule> calibrate) : ICheck
{
    public string Name => "review";

    public string Description =>
        $"model-judged rules on {targets.Sum(target => target.Passages.Count)} passages in {targets.Count} files"
        + (calibrate.Count > 0 ? $", calibrating {string.Join(", ", calibrate.Select(rule => rule.Id))}" : "");

    public static async Task<ReviewCheck?> ForChangesAsync(
        RepositoryContext repository, ModelAccess models, string baseRef, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var rules = ReviewRules.Load(repository);
        var targets = new List<ReviewTarget>();
        foreach (var path in paths.Where(path => ReviewRules.Reviewable(path, rules)))
        {
            var file = repository.Path(path);
            if (!File.Exists(file)) continue;
            var before = await repository.Git.ShowAsync(baseRef, path, cancellationToken);
            var passages = Passages.Added(before, await File.ReadAllTextAsync(file, cancellationToken));
            if (passages.Count > 0) targets.Add(new ReviewTarget(path, passages));
        }
        return Create(repository, models, rules, targets, ReviewRules.Changed(rules, paths));
    }

    public static async Task<ReviewCheck?> WholeAsync(
        RepositoryContext repository, ModelAccess models, IReadOnlyList<ReviewRule> rules, IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        var targets = new List<ReviewTarget>();
        foreach (var path in paths.Where(path => ReviewRules.Reviewable(path, rules)))
        {
            var passages = Passages.Split(await File.ReadAllTextAsync(repository.Path(path), cancellationToken));
            if (passages.Count > 0) targets.Add(new ReviewTarget(path, passages));
        }
        return Create(repository, models, rules, targets, []);
    }

    private static ReviewCheck? Create(
        RepositoryContext repository, ModelAccess models, IReadOnlyList<ReviewRule> rules,
        List<ReviewTarget> targets, IReadOnlyList<ReviewRule> calibrate) =>
        targets.Count == 0 && calibrate.Count == 0 ? null : new ReviewCheck(repository, models, rules, targets, calibrate);

    public async Task<CheckOutcome> RunAsync(Run run, CancellationToken cancellationToken)
    {
        if (models.Client() is not { } client)
            return new CheckOutcome(0, "[grey]no OpenAI API key[/]")
            {
                Mark = Outcome.Skipped,
                Details = console => Terminal.Hint(console, ModelAccess.SetupHint),
            };
        var result = await new Reviewer(repository, client).ReviewAsync(rules, targets, calibrate, cancellationToken);
        await Reviewer.SaveAsync(run, result);
        return new CheckOutcome(result.Report.Completed ? 0 : 1, Verdict(result.Report), result.Report)
        {
            Details = console => WriteFindings(console, result.Report),
        };
    }

    internal static string Verdict(ReviewReport report)
    {
        if (!report.Completed) return Terminal.Verdict(Outcome.Failed, "incomplete");
        var found = report.Findings.Count + report.Misses.Count;
        return found > 0
            ? Terminal.Verdict(Outcome.Attention, $"{found} {(found == 1 ? "finding" : "findings")}")
            : Terminal.Verdict(Outcome.Passed, "nothing found");
    }

    internal static void WriteFindings(IAnsiConsole console, ReviewReport report)
    {
        if (report.Findings.Count + report.Misses.Count > 0) console.WriteLine();
        Terminal.Rows(console, report.Findings.Select(finding => new[]
        {
            $"  [yellow]{Terminal.Esc(finding.Title)}[/]",
            Terminal.Esc($"{finding.Path}:{finding.Line}"),
            $"[grey]{Probability(finding.Probability)}[/]",
            $"[grey]{Terminal.Esc(finding.Excerpt)}[/]",
        }).Concat(report.Misses.Select(miss => new[]
        {
            $"  [yellow]{Terminal.Esc(miss.Title)}[/]",
            miss.Expected ? "answered no to a yes example" : "answered yes to a no example",
            $"[grey]{Probability(miss.Probability)}[/]",
            $"[grey]{Terminal.Esc(Terminal.Excerpt(miss.Example))}[/]",
        })));
        if (report.Failures.Count > 0) console.WriteLine();
        foreach (var failure in report.Failures) console.MarkupLine($"    {Terminal.Mark(Outcome.Failed)} {Terminal.Esc(failure)}");
    }

    private static string Probability(double? value) =>
        value is { } probability ? probability.ToString("0.00", CultureInfo.InvariantCulture) : "refused";
}
