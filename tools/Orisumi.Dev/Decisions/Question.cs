using System.Text.Json;
using System.Text.RegularExpressions;

namespace Orisumi.Dev;

internal enum QuestionType { Predicate, Choice }

internal sealed record QuestionOption(string Value, string? Description);

internal sealed record Question(
    string Id,
    string Title,
    QuestionType Type,
    string Instructions,
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> Findings,
    IReadOnlyList<QuestionOption> Options,
    double Threshold,
    string File)
{
    public const string Yes = "yes";
    public const string No = "no";

    public bool Reviews(string path) => Paths.Any(pattern => Glob.Matches(pattern, path));

    public bool Explains(string finding) => Findings.Any(pattern => Glob.Matches(pattern, finding));

    public bool Passes(Answer answer, string expected) => answer.ProbabilityOf(this, expected) >= Threshold;

}

internal static class Questions
{
    public const string Directory = "Questions/";
    private const double DefaultThreshold = 0.5;

    private sealed record OptionFile(string? Value, string? Description);

    private sealed record QuestionFile(
        string? Title, string? Type, string? Question, string[]? Paths, string[]? Findings,
        double? Threshold, OptionFile[]? Options);

    public static IReadOnlyList<Question> Load(RepositoryContext repository) => Load(repository, ProjectLayout.Load(repository.Root));

    public static IReadOnlyList<Question> Load(RepositoryContext repository, ProjectLayout layout)
    {
        var questions = layout.Projects
            .Select(project => project.Directory + Directory)
            .Distinct(StringComparer.Ordinal)
            .Where(directory => System.IO.Directory.Exists(repository.Path(directory)))
            .SelectMany(directory => System.IO.Directory.EnumerateFiles(repository.Path(directory), "*.json")
                .Order(StringComparer.Ordinal)
                .Select(path => Parse(path, directory + System.IO.Path.GetFileName(path))))
            .ToArray();
        if (questions.GroupBy(question => question.Id).FirstOrDefault(group => group.Count() > 1) is { } duplicate)
            throw new InvalidOperationException($"Question {duplicate.Key} is defined in {string.Join(" and ", duplicate.Select(question => question.File))}.");
        return questions;
    }

    private static Question Parse(string full, string path)
    {
        var id = System.IO.Path.GetFileNameWithoutExtension(full);
        var file = JsonSerializer.Deserialize<QuestionFile>(System.IO.File.ReadAllText(full), JsonOptions.Indented)
            ?? throw Invalid(path, "is empty");
        var type = file.Type switch
        {
            null or "predicate" => QuestionType.Predicate,
            "choice" => QuestionType.Choice,
            _ => throw Invalid(path, $"has unknown type '{file.Type}'; types are predicate and choice"),
        };
        if (file is not { Title.Length: > 0, Question.Length: > 0 }) throw Invalid(path, "needs title and question");
        var options = type == QuestionType.Predicate ? [] : Options(path, file.Options);
        return new Question(id, file.Title, type, file.Question, file.Paths ?? [], file.Findings ?? [], options,
            file.Threshold ?? DefaultThreshold, path);
    }

    private static QuestionOption[] Options(string path, OptionFile[]? entries)
    {
        if (entries is not { Length: >= 2 }) throw Invalid(path, "needs at least two options");
        return [.. entries.Select(entry => entry.Value is { Length: > 0 } value
            ? new QuestionOption(value, entry.Description) : throw Invalid(path, "has an option without a value"))];
    }

    private static InvalidOperationException Invalid(string path, string problem) => new($"{path} {problem}.");
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
