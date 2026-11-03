using System.Text.Json;
using System.Text.RegularExpressions;

namespace Orisumi.Dev;

internal enum RuleKind { Writing, Noise }

internal sealed record ReviewRule(
    string Id,
    string Title,
    RuleKind Kind,
    IReadOnlyList<string> Covers,
    string Question,
    IReadOnlyList<string> Yes,
    IReadOnlyList<string> No)
{
    public bool Applies(string subject) => Covers.Any(pattern => Glob.Matches(pattern, subject));
}

internal static class ReviewRules
{
    public const string WritingDirectory = "tools/Orisumi.Dev/Review/Rules/";
    public const string NoiseDirectory = "tools/Orisumi.Dev/Review/Noise/";

    private sealed record RuleFile(
        string? Title, string[]? Paths, string[]? Findings, string? Question, string[]? Yes, string[]? No);

    public static IReadOnlyList<ReviewRule> Load(RepositoryContext repository) =>
        [.. Load(repository, WritingDirectory, RuleKind.Writing), .. Load(repository, NoiseDirectory, RuleKind.Noise)];

    private static IEnumerable<ReviewRule> Load(RepositoryContext repository, string relative, RuleKind kind)
    {
        var directory = repository.Path(relative);
        return !Directory.Exists(directory) ? [] : Directory.EnumerateFiles(directory, "*.json")
            .Order(StringComparer.Ordinal)
            .Select(path => Parse(path, kind));
    }

    public static bool Reviewable(string path, IReadOnlyList<ReviewRule> rules) =>
        !ProjectLayout.IsScratch(path) && rules.Any(rule => rule.Kind == RuleKind.Writing && rule.Applies(path));

    public static IReadOnlyList<ReviewRule> Changed(IReadOnlyList<ReviewRule> rules, IEnumerable<string> paths)
    {
        var changed = paths.ToHashSet(StringComparer.Ordinal);
        return [.. rules.Where(rule => changed.Contains(PathOf(rule)))];
    }

    public static string PathOf(ReviewRule rule) =>
        (rule.Kind == RuleKind.Writing ? WritingDirectory : NoiseDirectory) + rule.Id + ".json";

    private static ReviewRule Parse(string path, RuleKind kind)
    {
        var file = JsonSerializer.Deserialize<RuleFile>(File.ReadAllText(path), JsonOptions.Indented);
        var id = Path.GetFileNameWithoutExtension(path);
        var covers = kind == RuleKind.Writing ? file?.Paths : file?.Findings;
        var coverField = kind == RuleKind.Writing ? "paths" : "findings";
        if (file is not { Title.Length: > 0, Question.Length: > 0, Yes.Length: > 0, No.Length: > 0 } || covers is not { Length: > 0 })
            throw new InvalidOperationException($"Rule {id} needs title, {coverField}, question, yes and no.");
        return new ReviewRule(id, file.Title, kind, covers, file.Question, file.Yes, file.No);
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
