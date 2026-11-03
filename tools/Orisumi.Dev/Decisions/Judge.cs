using System.Collections.Concurrent;
using System.Text.Json;

namespace Orisumi.Dev;

internal sealed record Judging(
    IReadOnlyDictionary<(string Input, string Question), Answer> Answers,
    IReadOnlyList<string> Failures,
    IReadOnlyList<DecisionCall> Calls)
{
    public Answer? Answer(string input, Question question) => Answers.GetValueOrDefault((input, question.Id));
}

internal sealed class Judge(string model, IDecisionClient? client, CacheStore? cache = null)
{
    private const int Parallelism = 8;
    private const int QuestionsPerRequest = 128;

    public async Task<Judging> AskAsync(IReadOnlyList<(string Input, Question Question)> questions, CancellationToken cancellationToken)
    {
        var answers = new ConcurrentDictionary<(string Input, string Question), Answer>();
        var pending = new List<(string Input, Question Question)>();
        foreach (var question in questions.Distinct())
        {
            if (cache?.Find(AnswerKey.Of(model, question.Question, question.Input)) is { } cached) answers[(question.Input, question.Question.Id)] = cached;
            else pending.Add(question);
        }
        var failures = new ConcurrentQueue<string>();
        var calls = new ConcurrentQueue<DecisionCall>();
        if (client is null && pending.Count > 0)
            failures.Enqueue($"{pending.Count} {(pending.Count == 1 ? "question needs" : "questions need")} the model and no OpenAI API key is set");
        var requests = client is null ? [] : pending.GroupBy(question => question.Input, StringComparer.Ordinal)
            .SelectMany(group => group.Chunk(QuestionsPerRequest)
                .Select(chunk => (Input: group.Key, Questions: chunk.Select(question => question.Question).ToArray())))
            .ToArray();
        var options = new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(requests, options, async (request, token) =>
        {
            try
            {
                var result = await client!.DecideAsync(request.Input, request.Questions, token);
                foreach (var call in result.Calls) calls.Enqueue(call);
                foreach (var question in request.Questions)
                {
                    if (result.Answers.FirstOrDefault(candidate => candidate.Name == question.Id) is not { } answer)
                    {
                        failures.Enqueue($"The model returned no answer for {question.Id}.");
                        continue;
                    }
                    answers[(request.Input, question.Id)] = answer.Answer;
                    cache?.Keep(AnswerKey.Of(model, question, request.Input), answer.Answer);
                }
            }
            catch (DecisionException exception)
            {
                foreach (var call in exception.Calls) calls.Enqueue(call);
                failures.Enqueue(exception.Message);
            }
        });
        return new Judging(answers, failures.Distinct(StringComparer.Ordinal).ToArray(), calls.ToArray());
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
}
