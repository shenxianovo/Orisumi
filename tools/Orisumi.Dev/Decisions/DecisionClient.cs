using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Orisumi.Dev;

internal sealed record DecisionAnswer(string Name, Answer Answer);

internal sealed record DecisionCall(string Request, int Status, string Response, TimeSpan Elapsed);

internal sealed record DecisionResult(IReadOnlyList<DecisionAnswer> Answers, IReadOnlyList<DecisionCall> Calls);

internal interface IDecisionClient
{
    string Model { get; }

    Task<DecisionResult> DecideAsync(string input, IReadOnlyList<Question> questions, CancellationToken cancellationToken);
}

internal sealed class DecisionException(string message, IReadOnlyList<DecisionCall> calls) : Exception(message)
{
    public IReadOnlyList<DecisionCall> Calls { get; } = calls;
}

internal sealed record ModelAccess(UserConfig Config, Func<string, IDecisionClient> Connect)
{
    public static ModelAccess Default { get; } = new(UserConfig.Default, key => new OpenAIDecisionClient(key));

    public IDecisionClient? Client() => Config.Get(UserConfig.OpenAIApiKey) is { Length: > 0 } key ? Connect(key) : null;

    public const string SetupHint = "orisumi config set openai.api-key";
}

internal sealed class OpenAIDecisionClient(string apiKey) : IDecisionClient
{
    private const int Attempts = 3;

    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri("https://api.openai.com/v1/"),
        Timeout = TimeSpan.FromSeconds(60),
    };

    public const string DefaultModel = "gpt-6-luna";

    public string Model => DefaultModel;

    public async Task<DecisionResult> DecideAsync(
        string input, IReadOnlyList<Question> questions, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new { model = Model, input, questions = questions.Select(Request) }, JsonOptions.Compact);
        var calls = new List<DecisionCall>();
        for (var attempt = 1; ; attempt++)
        {
            var clock = Stopwatch.StartNew();
            using var request = new HttpRequestMessage(HttpMethod.Post, "decisions")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            HttpResponseMessage response;
            try { response = await Http.SendAsync(request, cancellationToken); }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                calls.Add(new DecisionCall(body, 0, exception.Message, clock.Elapsed));
                if (attempt < Attempts) { await Backoff(attempt, cancellationToken); continue; }
                throw new DecisionException($"Decisions API unreachable: {exception.Message}", calls);
            }
            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                calls.Add(new DecisionCall(body, (int)response.StatusCode, text, clock.Elapsed));
                if (response.IsSuccessStatusCode) return new DecisionResult(Parse(text), calls);
                if (Retryable(response.StatusCode) && attempt < Attempts) { await Backoff(attempt, cancellationToken); continue; }
                throw new DecisionException($"Decisions API returned {(int)response.StatusCode}: {ErrorMessage(text)}", calls);
            }
        }
    }

    private static bool Retryable(HttpStatusCode status) => status == HttpStatusCode.TooManyRequests || (int)status >= 500;

    private static Task Backoff(int attempt, CancellationToken cancellationToken) =>
        Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);

    internal static object Request(Question question) => question.Type switch
    {
        QuestionType.Predicate => new { type = "predicate", name = question.Id, instructions = question.Instructions },
        _ => (object)new
        {
            type = "choice",
            name = question.Id,
            instructions = question.Instructions,
            choices = question.Options.Select(option => new { value = option.Value, description = option.Description }),
        },
    };

    internal static IReadOnlyList<DecisionAnswer> Parse(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.GetProperty("answers").EnumerateArray()
            .Select(answer => new DecisionAnswer(answer.GetProperty("name").GetString()!, ParseAnswer(answer))).ToArray();
    }

    private static Answer ParseAnswer(JsonElement answer) => answer.GetProperty("type").GetString() switch
    {
        "predicate" => new Answer(new Dictionary<string, double> { [Question.Yes] = answer.GetProperty("probability").GetDouble() }),
        "choice" => new Answer(answer.GetProperty("probabilities").EnumerateArray().ToDictionary(
            entry => entry.GetProperty("value").GetString()!, entry => entry.GetProperty("probability").GetDouble(), StringComparer.Ordinal)),
        _ => Answer.Refusal,
    };

    private static string ErrorMessage(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.GetProperty("error").GetProperty("message").GetString() ?? text;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return text.Length > 200 ? text[..200] : text;
        }
    }
}
