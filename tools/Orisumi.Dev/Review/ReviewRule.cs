using System.Text.Json;
using System.Text.RegularExpressions;

namespace Orisumi.Dev;

internal sealed record ReviewRule(
    string Id,
    string Title,
    IReadOnlyList<string> Paths,
    string Question,
    IReadOnlyList<string> Violations,
    IReadOnlyList<string> Conforming)
{
    public bool Covers(string path) => Paths.Any(pattern => Glob.Matches(pattern, path));
}

internal static class ReviewRules
{
    public const string Directory = "tools/Orisumi.Dev/Review/Rules/";

    private sealed record RuleFile(
        string? Title, string[]? Paths, string? Question, string[]? Violations, string[]? Conforming);

    public static IReadOnlyList<ReviewRule> Load(RepositoryContext repository)
    {
        var directory = repository.Path(Directory);
        if (!System.IO.Directory.Exists(directory)) return [];
        return System.IO.Directory.EnumerateFiles(directory, "*.json")
            .Order(StringComparer.Ordinal)
            .Select(Parse)
            .ToArray();
    }

    public static string? IdOf(string path) =>
        path.StartsWith(Directory, StringComparison.Ordinal) && path.EndsWith(".json", StringComparison.Ordinal)
            ? Path.GetFileNameWithoutExtension(path) : null;

    private static ReviewRule Parse(string path)
    {
        var file = JsonSerializer.Deserialize<RuleFile>(File.ReadAllText(path), JsonOptions.Indented);
        var id = Path.GetFileNameWithoutExtension(path);
        if (file is not { Title.Length: > 0, Paths.Length: > 0, Question.Length: > 0, Violations.Length: > 0, Conforming.Length: > 0 })
            throw new InvalidOperationException($"Review rule {id} needs title, paths, question, violations and conforming.");
        return new ReviewRule(id, file.Title, file.Paths, file.Question, file.Violations, file.Conforming);
    }
}

internal static class Glob
{
    private static readonly Dictionary<string, Regex> Cache = [];

    public static bool Matches(string pattern, string path)
    {
        Regex? regex;
        lock (Cache)
        {
            if (!Cache.TryGetValue(pattern, out regex)) Cache[pattern] = regex = Compile(pattern);
        }
        return regex.IsMatch(path);
    }

    private static Regex Compile(string pattern)
    {
        var expression = Regex.Escape(pattern)
            .Replace(@"\*\*/", "(?:.*/)?", StringComparison.Ordinal)
            .Replace(@"\*\*", ".*", StringComparison.Ordinal)
            .Replace(@"\*", "[^/]*", StringComparison.Ordinal)
            .Replace(@"\?", "[^/]", StringComparison.Ordinal);
        return new Regex($"^{expression}$", RegexOptions.CultureInvariant);
    }
}
