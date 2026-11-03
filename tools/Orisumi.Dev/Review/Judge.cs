using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Orisumi.Dev;

internal sealed record Judging(
    IReadOnlyDictionary<(string Input, string Rule), double?> Answers,
    int Cached,
    IReadOnlyList<string> Failures,
    IReadOnlyList<DecisionCall> Calls)
{
    public bool Answered(string input, ReviewRule rule) => Answers.ContainsKey((input, rule.Id));

    public double? Answer(string input, ReviewRule rule) => Answers.GetValueOrDefault((input, rule.Id));

    public int Refused => Answers.Values.Count(value => value is null);
}

internal sealed class Judge(RepositoryContext repository, IDecisionClient client)
{
    public const double Threshold = 0.5;
    private const int Parallelism = 8;
    private const int QuestionsPerRequest = 128;

    private sealed record CachedAnswer(double? Probability);

    private string CacheRoot => repository.Path(".artifacts", "review-cache");

    public string Model => client.Model;

    public async Task<Judging> AskAsync(IReadOnlyList<(string Input, ReviewRule Rule)> questions, CancellationToken cancellationToken)
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

    public static Task SaveCallsAsync(Run run, string file, IReadOnlyList<DecisionCall> calls) =>
        File.WriteAllLinesAsync(run.PathOf(file),
            calls.Select(call => JsonSerializer.Serialize(new
            {
                status = call.Status,
                elapsedMs = (int)call.Elapsed.TotalMilliseconds,
                request = Json(call.Request),
                response = Json(call.Response),
            }, JsonOptions.Compact)), CancellationToken.None);

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
