namespace Orisumi.Dev;

internal sealed record ReviewTarget(string Path, IReadOnlyList<Passage> Passages);

internal sealed record ReviewFinding(string Path, int Line, string Rule, string Title, double Probability, string Excerpt);

internal sealed record CalibrationMiss(string Rule, string Title, string Example, bool Expected, double? Probability);

internal sealed record ReviewJudgment(string Path, int Line, string Rule, double? Probability);

internal sealed record ReviewReport(
    string Model,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Calibrated,
    int Questions,
    int Cached,
    int Refused,
    IReadOnlyList<ReviewFinding> Findings,
    IReadOnlyList<CalibrationMiss> Misses,
    IReadOnlyList<ReviewJudgment> Judgments,
    IReadOnlyList<string> Failures)
{
    public bool Completed => Failures.Count == 0;
}

internal sealed record ReviewResult(ReviewReport Report, IReadOnlyList<DecisionCall> Calls);

internal sealed class Reviewer(RepositoryContext repository, IDecisionClient client)
{
    public async Task<ReviewResult> ReviewAsync(
        IReadOnlyList<ReviewRule> rules,
        IReadOnlyList<ReviewTarget> targets,
        IReadOnlyList<ReviewRule> calibrate,
        CancellationToken cancellationToken)
    {
        var passages = targets.SelectMany(target => target.Passages.SelectMany(passage =>
            rules.Where(rule => rule.Kind == RuleKind.Writing && rule.Applies(target.Path))
                .Select(rule => (target.Path, Passage: passage, Rule: rule)))).ToArray();
        var examples = calibrate.SelectMany(rule =>
            rule.Yes.Select(text => (Rule: rule, Text: text, Expected: true))
                .Concat(rule.No.Select(text => (Rule: rule, Text: text, Expected: false)))).ToArray();
        var questions = passages.Select(item => (Input: item.Passage.Text, item.Rule))
            .Concat(examples.Select(item => (Input: item.Text, item.Rule)))
            .Distinct()
            .ToArray();
        var judging = await new Judge(repository, client).AskAsync(questions, cancellationToken);
        double? Answer(string input, ReviewRule rule) => judging.Answer(input, rule);
        bool Answered(string input, ReviewRule rule) => judging.Answered(input, rule);

        var judgments = passages.Where(item => Answered(item.Passage.Text, item.Rule))
            .Select(item => new ReviewJudgment(item.Path, item.Passage.Line, item.Rule.Id, Answer(item.Passage.Text, item.Rule)))
            .ToArray();
        var findings = passages
            .Where(item => Answer(item.Passage.Text, item.Rule) >= Judge.Threshold)
            .Select(item => new ReviewFinding(item.Path, item.Passage.Line, item.Rule.Id, item.Rule.Title,
                Answer(item.Passage.Text, item.Rule)!.Value, Terminal.Excerpt(item.Passage.Text)))
            .OrderBy(finding => finding.Path, StringComparer.Ordinal).ThenBy(finding => finding.Line)
            .ToArray();
        var misses = examples
            .Where(item => Answered(item.Text, item.Rule) && Answer(item.Text, item.Rule) is var probability
                && (probability is null || probability >= Judge.Threshold != item.Expected))
            .Select(item => new CalibrationMiss(item.Rule.Id, item.Rule.Title, item.Text, item.Expected, Answer(item.Text, item.Rule)))
            .ToArray();
        var report = new ReviewReport(
            client.Model,
            targets.Select(target => target.Path).ToArray(),
            calibrate.Select(rule => rule.Id).ToArray(),
            questions.Length,
            judging.Cached,
            judging.Refused,
            findings,
            misses,
            judgments,
            judging.Failures);
        return new ReviewResult(report, judging.Calls);
    }

    public static async Task SaveAsync(Run run, ReviewResult result)
    {
        await run.WriteJsonAsync(RunFiles.Review, result.Report);
        await Judge.SaveCallsAsync(run, RunFiles.ReviewCalls, result.Calls);
    }
}
