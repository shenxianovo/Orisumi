using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Orisumi.Dev;

internal static class AnswerKey
{
    public static string Of(string model, Question question, string input) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{model}\n{JsonSerializer.Serialize(OpenAIDecisionClient.Request(question), JsonOptions.Compact)}\n{input}")));
}

internal sealed class CacheStore(RepositoryContext repository)
{
    private string Root => repository.Path(".artifacts", "review-cache");

    public Answer? Find(string key)
    {
        var path = Path.Combine(Root, key + ".json");
        return File.Exists(path) && JsonSerializer.Deserialize<Answer>(File.ReadAllText(path), JsonOptions.Indented) is { } cached ? cached : null;
    }

    public void Keep(string key, Answer answer)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, key + ".json"), JsonSerializer.Serialize(answer, JsonOptions.Indented));
    }
}
