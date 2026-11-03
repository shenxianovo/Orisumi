using System.CommandLine;
using System.CommandLine.Help;
using System.Globalization;
using System.Text.Json;
using Spectre.Console;

namespace Orisumi.Dev;

internal sealed class ReviewCommand(RepositoryContext repository, IProcessRunner runner, ModelAccess models, TextWriter output)
{
    public const string Limitation = "Model judgments guide review; findings do not fail the run.";

    public Command CreateCommand()
    {
        var command = new Command("review", "Judge writing against rules that need a model's judgment");
        command.SetAction(parse => new HelpAction().Invoke(parse));

        var check = new Command("check", "Review changed passages by default, or the given files, or every covered file");
        var paths = new Argument<string[]>("paths") { Arity = ArgumentArity.ZeroOrMore, Description = "Files to review in full" };
        var baseRef = new Option<string?>("--base") { Description = "Git comparison base for changed passages" };
        var all = new Option<bool>("--all") { Description = "Review every file a rule covers" };
        var checkJson = JsonOption();
        check.Arguments.Add(paths);
        check.Options.Add(baseRef);
        check.Options.Add(all);
        check.Options.Add(checkJson);
        check.SetAction((parse, token) => CheckAsync(
            parse.GetValue(paths) ?? [], parse.GetValue(baseRef), parse.GetValue(all), parse.GetValue(checkJson), token));

        var rules = new Command("rules", "List the rules and the files each one covers");
        var rulesJson = JsonOption();
        rules.Options.Add(rulesJson);
        rules.SetAction(parse => RulesAsync(parse.GetValue(rulesJson)));

        var calibrate = new Command("calibrate", "Judge each rule's own examples to show it catches what it should");
        var ids = new Argument<string[]>("rules") { Arity = ArgumentArity.ZeroOrMore, Description = "Rule ids; all rules when omitted" };
        var calibrateJson = JsonOption();
        calibrate.Arguments.Add(ids);
        calibrate.Options.Add(calibrateJson);
        calibrate.SetAction((parse, token) => CalibrateAsync(parse.GetValue(ids) ?? [], parse.GetValue(calibrateJson), token));

        command.Subcommands.Add(check);
        command.Subcommands.Add(rules);
        command.Subcommands.Add(calibrate);
        return command;
    }

    private static Option<bool> JsonOption() => new("--json") { Description = "Print JSON" };

    private async Task<int> CheckAsync(string[] paths, string? baseRef, bool all, bool json, CancellationToken token)
    {
        if ((paths.Length > 0 ? 1 : 0) + (all ? 1 : 0) + (baseRef is not null ? 1 : 0) > 1)
            throw new CommandUsageException("Choose one of: paths, --all, --base.");
        var rules = ReviewRules.Load(repository);
        if (all)
        {
            var files = await ReviewSelection.WorkspaceFilesAsync(runner, token);
            return await RunAsync("every covered file", rules, await ReviewSelection.WholeAsync(repository, rules, files, token), [], json, token);
        }
        if (paths.Length > 0)
        {
            var relative = paths.Select(RepositoryPath).ToArray();
            return await RunAsync($"{relative.Length} {(relative.Length == 1 ? "file" : "files")}", rules,
                await ReviewSelection.WholeAsync(repository, rules, relative, token), [], json, token);
        }
        var resolved = (await VerificationRequest.ResolveBaseAsync("changed", baseRef, runner, token))!;
        var changed = await VerificationPlanner.ChangedPathsAsync(runner, resolved, token);
        return await RunAsync($"changed since {resolved}", rules,
            await ReviewSelection.ChangedAsync(repository, runner, rules, resolved, changed, token),
            ReviewSelection.ChangedRules(rules, changed), json, token);
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
        var selected = ids.Length == 0 ? rules : rules.Where(rule => ids.Contains(rule.Id)).ToArray();
        return await RunAsync("calibration", rules, [], selected, json, token);
    }

    private async Task<int> RunAsync(
        string scope, IReadOnlyList<ReviewRule> rules, IReadOnlyList<ReviewTarget> targets,
        IReadOnlyList<ReviewRule> calibrate, bool json, CancellationToken token)
    {
        var console = Terminal.Create(output);
        if (targets.Count == 0 && calibrate.Count == 0)
        {
            if (json) await output.WriteLineAsync(JsonSerializer.Serialize(new { evidence = (string?)null, review = (ReviewReport?)null }, JsonOptions.Indented));
            else console.MarkupLine($"{Terminal.Mark(Outcome.Passed)} Nothing to review{Terminal.Dot}[grey]{Terminal.Esc(scope)} has no passages a rule covers[/]");
            return 0;
        }
        if (models.Client() is not { } client)
        {
            Terminal.Error(output, "No OpenAI API key");
            Terminal.Hint(console, ModelAccess.SetupHint);
            return 1;
        }
        return await EvidenceSession.ExecuteAsync(repository, "review", "model", [Limitation], async evidence =>
        {
            var reviewer = new Reviewer(repository, client);
            var report = json
                ? await reviewer.ReviewAsync(rules, targets, calibrate, evidence.Run.Directory, token)
                : await Terminal.SpinAsync(console, "Reviewing",
                    () => reviewer.ReviewAsync(rules, targets, calibrate, evidence.Run.Directory, token));
            if (json)
                await output.WriteLineAsync(JsonSerializer.Serialize(new { evidence = evidence.Run.Directory, review = report }, JsonOptions.Indented));
            else
                Write(console, scope, report, evidence.Run.Directory);
            return report.Completed ? 0 : 1;
        }, notes: json ? null : output);
    }

    private async Task<int> RulesAsync(bool json)
    {
        var rules = ReviewRules.Load(repository);
        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new { directory = ReviewRules.Directory, rules }, JsonOptions.Indented));
            return 0;
        }
        var console = Terminal.Create(output);
        Terminal.Heading(console, "Review rules", $"{rules.Count} {(rules.Count == 1 ? "rule" : "rules")}", Terminal.Esc(ReviewRules.Directory));
        Terminal.Rows(console, rules.Select(rule => new[]
        {
            Terminal.Esc(rule.Id),
            Terminal.Esc(rule.Title),
            $"[grey]{Terminal.Esc(string.Join(", ", rule.Paths))}[/]",
            $"[grey]{rule.Violations.Count} violating · {rule.Conforming.Count} conforming examples[/]",
        }));
        return 0;
    }

    internal static string Verdict(ReviewReport report)
    {
        if (!report.Completed) return Terminal.Verdict(Outcome.Failed, "incomplete");
        var found = report.Findings.Count + report.Misses.Count;
        return found > 0
            ? Terminal.Verdict(Outcome.Attention, $"{found} {(found == 1 ? "finding" : "findings")}")
            : Terminal.Verdict(Outcome.Passed, "nothing found");
    }

    private void Write(IAnsiConsole console, string scope, ReviewReport report, string evidence)
    {
        Terminal.Heading(console, "Review", Terminal.Esc(scope), Verdict(report));
        var cached = report.Cached > 0 ? $", {report.Cached} cached" : "";
        var refused = report.Refused > 0 ? $", {report.Refused} refused" : "";
        console.MarkupLine($"  [grey]{Count(report.Files.Count, "file")} · {Count(report.Calibrated.Count, "rule")} calibrated · "
            + $"{Count(report.Questions, "question")}{cached}{refused} · {Terminal.Esc(report.Model)}[/]");
        WriteFindings(console, report);
        console.WriteLine();
        console.MarkupLine($"  [grey]evidence[/]  {Terminal.Esc(Terminal.Path(repository, Path.Combine(evidence, "review.json")))}");
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
            miss.Violation ? "missed a violation" : "flagged a conforming example",
            $"[grey]{Probability(miss.Probability)}[/]",
            $"[grey]{Terminal.Esc(Reviewer.Excerpt(miss.Example))}[/]",
        })));
        if (report.Failures.Count > 0) console.WriteLine();
        foreach (var failure in report.Failures) console.MarkupLine($"    {Terminal.Mark(Outcome.Failed)} {Terminal.Esc(failure)}");
    }

    private static string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    private static string Probability(double? value) =>
        value is { } probability ? probability.ToString("0.00", CultureInfo.InvariantCulture) : "refused";
}
