using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Orisumi.Dev;

internal sealed record ReviewTarget(string Path, IReadOnlyList<Passage> Passages);

internal sealed record ReviewFinding(string Path, int Line, string Rule, string Title, double Probability, string Excerpt);

internal sealed record CalibrationMiss(string Rule, string Title, string Example, bool Violation, double? Probability);

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
    public const double Threshold = 0.5;
    private const int Parallelism = 8;
    private const int QuestionsPerRequest = 128;

    private sealed record CachedAnswer(double? Probability);

    private sealed record Judging(
        IReadOnlyDictionary<(string Input, string Rule), double?> Answers,
        int Cached,
        IReadOnlyList<string> Failures,
        IReadOnlyList<DecisionCall> Calls);

    private string CacheRoot => repository.Path(".artifacts", "review-cache");

    public async Task<ReviewResult> ReviewAsync(
        IReadOnlyList<ReviewRule> rules,
        IReadOnlyList<ReviewTarget> targets,
        IReadOnlyList<ReviewRule> calibrate,
        CancellationToken cancellationToken)
    {
        var passages = targets.SelectMany(target => target.Passages.SelectMany(passage =>
            rules.Where(rule => rule.Covers(target.Path)).Select(rule => (target.Path, Passage: passage, Rule: rule)))).ToArray();
        var examples = calibrate.SelectMany(rule =>
            rule.Violations.Select(text => (Rule: rule, Text: text, Violation: true))
                .Concat(rule.Conforming.Select(text => (Rule: rule, Text: text, Violation: false)))).ToArray();
        var questions = passages.Select(item => (Input: item.Passage.Text, item.Rule))
            .Concat(examples.Select(item => (Input: item.Text, item.Rule)))
            .Distinct()
            .ToArray();
        var judging = await JudgeAsync(questions, cancellationToken);
        double? Answer(string input, ReviewRule rule) => judging.Answers.GetValueOrDefault((input, rule.Id));
        bool Answered(string input, ReviewRule rule) => judging.Answers.ContainsKey((input, rule.Id));

        var judgments = passages.Where(item => Answered(item.Passage.Text, item.Rule))
            .Select(item => new ReviewJudgment(item.Path, item.Passage.Line, item.Rule.Id, Answer(item.Passage.Text, item.Rule)))
            .ToArray();
        var findings = passages
            .Where(item => Answer(item.Passage.Text, item.Rule) >= Threshold)
            .Select(item => new ReviewFinding(item.Path, item.Passage.Line, item.Rule.Id, item.Rule.Title,
                Answer(item.Passage.Text, item.Rule)!.Value, Terminal.Excerpt(item.Passage.Text)))
            .OrderBy(finding => finding.Path, StringComparer.Ordinal).ThenBy(finding => finding.Line)
            .ToArray();
        var misses = examples
            .Where(item => Answered(item.Text, item.Rule) && Answer(item.Text, item.Rule) is var probability
                && (probability is null || probability >= Threshold != item.Violation))
            .Select(item => new CalibrationMiss(item.Rule.Id, item.Rule.Title, item.Text, item.Violation, Answer(item.Text, item.Rule)))
            .ToArray();
        var report = new ReviewReport(
            client.Model,
            targets.Select(target => target.Path).ToArray(),
            calibrate.Select(rule => rule.Id).ToArray(),
            questions.Length,
            judging.Cached,
            questions.Count(question => Answered(question.Input, question.Rule) && Answer(question.Input, question.Rule) is null),
            findings,
            misses,
            judgments,
            judging.Failures);
        return new ReviewResult(report, judging.Calls);
    }

    public static async Task SaveAsync(Run run, ReviewResult result)
    {
        await run.WriteJsonAsync(RunFiles.Review, result.Report);
        await File.WriteAllLinesAsync(run.PathOf(RunFiles.ReviewCalls),
            result.Calls.Select(call => JsonSerializer.Serialize(new
            {
                status = call.Status,
                elapsedMs = (int)call.Elapsed.TotalMilliseconds,
                request = Json(call.Request),
                response = Json(call.Response),
            }, JsonOptions.Compact)), CancellationToken.None);
    }

    private static object Json(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return text;
        }
    }

    private async Task<Judging> JudgeAsync(IReadOnlyList<(string Input, ReviewRule Rule)> questions, CancellationToken cancellationToken)
    {
        var answers = new ConcurrentDictionary<(string Input, string Rule), double?>();
        var pending = new List<(string Input, ReviewRule Rule)>();
        foreach (var question in questions)
        {
            if (ReadCache(question.Input, question.Rule) is { } cached) answers[(question.Input, question.Rule.Id)] = cached.Probability;
            else pending.Add(question);
        }
        var cachedCount = answers.Count;
        var failures = new ConcurrentQueue<string>();
        var calls = new ConcurrentQueue<DecisionCall>();
        var requests = pending.GroupBy(question => question.Input, StringComparer.Ordinal)
            .SelectMany(group => group.Chunk(QuestionsPerRequest)
                .Select(chunk => (Input: group.Key, Rules: chunk.Select(question => question.Rule).ToArray())))
            .ToArray();
        var options = new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(requests, options, async (request, token) =>
        {
            try
            {
                var result = await client.DecideAsync(request.Input,
                    request.Rules.Select(rule => new DecisionQuestion(rule.Id, rule.Question)).ToArray(), token);
                foreach (var call in result.Calls) calls.Enqueue(call);
                foreach (var rule in request.Rules)
                {
                    var answer = result.Answers.FirstOrDefault(candidate => candidate.Name == rule.Id);
                    if (answer is null)
                    {
                        failures.Enqueue($"The model returned no answer for {rule.Id}.");
                        continue;
                    }
                    answers[(request.Input, rule.Id)] = answer.Probability;
                    await WriteCacheAsync(request.Input, rule, answer.Probability, token);
                }
            }
            catch (DecisionException exception)
            {
                foreach (var call in exception.Calls) calls.Enqueue(call);
                failures.Enqueue(exception.Message);
            }
        });
        return new Judging(answers, cachedCount, failures.Distinct(StringComparer.Ordinal).ToArray(), calls.ToArray());
    }

    private string CachePath(string input, ReviewRule rule)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{client.Model}\n{rule.Question}\n{input}"));
        return Path.Combine(CacheRoot, Convert.ToHexStringLower(hash) + ".json");
    }

    private CachedAnswer? ReadCache(string input, ReviewRule rule)
    {
        var path = CachePath(input, rule);
        return File.Exists(path) ? JsonSerializer.Deserialize<CachedAnswer>(File.ReadAllText(path), JsonOptions.Indented) : null;
    }

    private async Task WriteCacheAsync(string input, ReviewRule rule, double? probability, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(CacheRoot);
        await File.WriteAllTextAsync(CachePath(input, rule),
            JsonSerializer.Serialize(new CachedAnswer(probability), JsonOptions.Indented), cancellationToken);
    }
}
