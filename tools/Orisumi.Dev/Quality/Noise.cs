using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Orisumi.Dev;

internal sealed partial record QualityFinding(string Rule, string Path, int Line, string Symbol)
{
    public const string Complexity = "complexity";

    public static QualityFinding Of(ComplexityHotspot hotspot) => new(Complexity, hotspot.Path, hotspot.Line, ShortSymbol(hotspot.Symbol));

    public static QualityFinding Of(DeadCodeFinding finding) => new(finding.Rule, finding.Path, finding.Line, finding.Symbol);

    public bool Matches(NoiseDeclaration declaration) =>
        Rule == declaration.Rule && Path == declaration.Path && Symbol == declaration.Symbol;

    internal static string ShortSymbol(string symbol)
    {
        var segments = Parameters().Replace(symbol, "").Split('/')
            .Select(segment => Arity().Replace(Kind().Replace(segment, ""), ""))
            .Where(segment => segment.Length > 0)
            .ToArray();
        return string.Join('.', segments.TakeLast(2));
    }

    [GeneratedRegex(@"\(.*\)")]
    private static partial Regex Parameters();

    [GeneratedRegex(@"^[A-Za-z]+:")]
    private static partial Regex Kind();

    [GeneratedRegex(@"`\d+")]
    private static partial Regex Arity();
}

internal sealed record NoiseDeclaration(string Rule, string Path, string Symbol, string Reason);

internal enum NoiseStatus { Unexplained, Explained, Declared, Contradicted }

internal sealed record NoiseJudgment(
    QualityFinding Finding, NoiseStatus Status, string? Reason, string? ReasonTitle, double? Probability, string? Declared, string Evidence);

internal sealed record NoiseReport(bool Judged, IReadOnlyList<NoiseJudgment> Judgments, IReadOnlyList<NoiseDeclaration> Stale)
{
    public bool IsFolded(QualityFinding finding) =>
        Judgments.Any(judgment => judgment.Status == NoiseStatus.Declared && judgment.Finding == finding);

    public NoiseJudgment? Of(QualityFinding finding) => Judgments.FirstOrDefault(judgment => judgment.Finding == finding);

    public IEnumerable<NoiseJudgment> With(NoiseStatus status) => Judgments.Where(judgment => judgment.Status == status);
}

internal sealed record VanishedFunction(string Path, string Symbol, int Complexity, string Source);

internal sealed record NoiseSources(
    IReadOnlyList<string> Files, IReadOnlyList<FunctionMetric> Functions, IReadOnlyList<VanishedFunction> Vanished);

internal sealed partial class NoiseJudge(RepositoryContext repository, ModelAccess models, ProjectLayout layout)
{
    public const string FileName = "noise.json";
    private const int MaxReferences = 15;
    private const int MaxSourceLines = 120;

    private sealed record DeclarationFile(string? Rule, string? Path, string? Symbol, string? Reason);

    private readonly Dictionary<string, string[]> _lines = new(StringComparer.Ordinal);

    public async Task<(NoiseReport Report, IReadOnlyList<string> Failures)> JudgeAsync(
        IReadOnlyList<QualityFinding> findings, NoiseSources sources, Run run, CancellationToken cancellationToken)
    {
        var reasons = ReviewRules.Load(repository).Where(rule => rule.Kind == RuleKind.Noise).ToArray();
        var failures = new List<string>();
        var declarations = Declarations(reasons, failures);
        var evidence = findings.Distinct().ToDictionary(finding => finding, finding => Evidence(finding, sources));
        var judging = await AskAsync([.. evidence.SelectMany(item => reasons.Where(reason => reason.Applies(item.Key.Rule))
            .Select(reason => (item.Value, reason)))], run, failures, cancellationToken);
        var judgments = evidence.Select(item => Classify(item.Key, item.Value,
            [.. reasons.Where(reason => reason.Applies(item.Key.Rule))], declarations.FirstOrDefault(item.Key.Matches)?.Reason, judging)).ToArray();
        var stale = declarations.Where(declaration => !findings.Any(finding => finding.Matches(declaration))).ToArray();
        failures.AddRange(stale.Select(declaration =>
            $"{declaration.Path} {declaration.Symbol} ({declaration.Rule}) is declared as noise but no longer reported; remove it from {FileName}"));
        return (new NoiseReport(judging is not null, judgments, stale), failures);
    }

    private async Task<Judging?> AskAsync(
        IReadOnlyList<(string Input, ReviewRule Rule)> questions, Run run, List<string> failures, CancellationToken cancellationToken)
    {
        if (models.Client() is not { } client) return null;
        var judging = await new Judge(repository, client).AskAsync(questions, cancellationToken);
        await Judge.SaveCallsAsync(run, RunFiles.NoiseCalls, judging.Calls);
        failures.AddRange(judging.Failures.Select(failure => $"noise judgment: {failure}"));
        return judging;
    }

    internal static NoiseJudgment Classify(
        QualityFinding finding, string evidence, IReadOnlyList<ReviewRule> reasons, string? declared, Judging? judging)
    {
        var answers = reasons.Select(reason => (Reason: reason, Probability: judging?.Answer(evidence, reason))).ToArray();
        var best = answers.Where(answer => answer.Probability >= Judge.Threshold).OrderByDescending(answer => answer.Probability).FirstOrDefault();
        var holds = answers.FirstOrDefault(answer => answer.Reason.Id == declared).Probability is not { } probability
            || probability >= Judge.Threshold;
        var status = (declared, best.Reason) switch
        {
            (not null, _) => holds ? NoiseStatus.Declared : NoiseStatus.Contradicted,
            (null, not null) => NoiseStatus.Explained,
            _ => NoiseStatus.Unexplained,
        };
        return new NoiseJudgment(finding, status, best.Reason?.Id, best.Reason?.Title, best.Probability, declared, evidence);
    }

    private List<NoiseDeclaration> Declarations(IReadOnlyList<ReviewRule> reasons, List<string> failures)
    {
        var declarations = new List<NoiseDeclaration>();
        foreach (var project in layout.Projects)
        {
            var path = repository.Path(project.Directory + FileName);
            if (!File.Exists(path)) continue;
            foreach (var entry in JsonSerializer.Deserialize<DeclarationFile[]>(File.ReadAllText(path), JsonOptions.Indented) ?? [])
            {
                if (entry is not { Rule.Length: > 0, Path.Length: > 0, Symbol.Length: > 0, Reason.Length: > 0 })
                    failures.Add($"{project.Directory}{FileName}: every entry needs rule, path, symbol and reason");
                else if (reasons.All(reason => reason.Id != entry.Reason))
                    failures.Add($"{project.Directory}{FileName}: unknown reason '{entry.Reason}'; reasons are {string.Join(", ", reasons.Select(reason => reason.Id))}");
                else declarations.Add(new NoiseDeclaration(entry.Rule, project.Directory + entry.Path, entry.Symbol, entry.Reason));
            }
        }
        return declarations;
    }

    internal string Evidence(QualityFinding finding, NoiseSources sources)
    {
        var project = layout.Owner(finding.Path);
        var text = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"发现：{finding.Rule}")
            .AppendLine(CultureInfo.InvariantCulture, $"位置：{finding.Path}:{finding.Line}")
            .AppendLine(CultureInfo.InvariantCulture, $"对象：{finding.Symbol}")
            .AppendLine(CultureInfo.InvariantCulture, $"所属项目：{project?.Name ?? "无"}");
        if (finding.Rule == QualityFinding.Complexity) AppendComplexity(text, finding, sources);
        else if (project is not null) AppendReferences(text, finding, project, sources.Files);
        return text.ToString().TrimEnd();
    }

    private void AppendComplexity(StringBuilder text, QualityFinding finding, NoiseSources sources)
    {
        var function = sources.Functions.FirstOrDefault(item => item.Path == finding.Path && item.Line == finding.Line);
        if (function is not null)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"复杂度：{function.Complexity}").AppendLine().AppendLine("函数源码：")
                .AppendLine(Source(Lines(finding.Path), function));
        }
        text.AppendLine().AppendLine("本次从基点消失的复杂函数：");
        if (sources.Vanished.Count == 0) text.AppendLine("（无）");
        foreach (var vanished in sources.Vanished)
            text.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"{vanished.Path} {vanished.Symbol} · {vanished.Complexity}").AppendLine(vanished.Source);
    }

    internal static string Source(string[] lines, FunctionMetric function) =>
        string.Join('\n', lines.Skip(function.Line - 1).Take(Math.Clamp(function.EndLine - function.Line + 1, 1, MaxSourceLines)));

    private void AppendReferences(StringBuilder text, QualityFinding finding, Project project, IReadOnlyList<string> files)
    {
        var term = Term(finding, project);
        var pattern = new Regex($"(?<![\\w@-]){Regex.Escape(term)}(?![\\w-])", RegexOptions.CultureInvariant);
        var hits = files
            .Where(path => project.Contains(path) && path != project.Manifest && !SourceLanguages.IsGenerated(path))
            .SelectMany(path => Lines(path).Select((line, index) => (Path: path, Line: index + 1, Text: line)))
            .Where(hit => !(hit.Path == finding.Path && hit.Line == finding.Line) && pattern.IsMatch(hit.Text))
            .ToArray();
        text.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"项目内对「{term}」的引用（{hits.Length} 处，不含项目清单和锁文件）：");
        Append(text, hits.Select(hit => $"{hit.Path}:{hit.Line}: {Clip(hit.Text.Trim())}"));
        if (finding.Rule is not ("knip/dependencies" or "knip/devDependencies")) return;
        var key = new Regex($"^\\s*'?{Regex.Escape(term)}(@[^:']*)?'?:", RegexOptions.CultureInvariant);
        text.AppendLine().AppendLine("锁文件中把它作为依赖项的位置：");
        Append(text, files.Where(path => project.Contains(path) && SourceLanguages.IsGenerated(path)).SelectMany(path => Chains(Lines(path), key)));
    }

    private static string Term(QualityFinding finding, Project project) => finding.Rule switch
    {
        "knip/files" => Path.ChangeExtension(finding.Path[project.Directory.Length..], null)!,
        "IDE0051" => finding.Symbol.Split('.')[^1],
        _ => finding.Symbol,
    };

    private static void Append(StringBuilder text, IEnumerable<string> lines)
    {
        var listed = lines.Take(MaxReferences).ToArray();
        if (listed.Length == 0) text.AppendLine("（无）");
        foreach (var line in listed) text.AppendLine(line);
    }

    private static IEnumerable<string> Chains(string[] lines, Regex key)
    {
        for (var index = 0; index < lines.Length; index++)
        {
            if (!key.IsMatch(lines[index])) continue;
            var chain = new List<string> { lines[index].Trim() };
            var indent = Indent(lines[index]);
            for (var parent = index - 1; parent >= 0 && chain.Count < 4 && indent > 0; parent--)
            {
                if (lines[parent].Trim().Length == 0 || Indent(lines[parent]) >= indent) continue;
                indent = Indent(lines[parent]);
                chain.Insert(0, lines[parent].Trim().TrimEnd(':'));
            }
            yield return string.Join(" > ", chain.Select(part => part.Length > 60 ? part[..60] + "…" : part));
        }
    }

    private static int Indent(string line) => line.Length - line.TrimStart().Length;

    private static string Clip(string text) => text.Length > 200 ? text[..200] + "…" : text;

    private string[] Lines(string path)
    {
        if (!_lines.TryGetValue(path, out var lines))
        {
            var full = repository.Path(path);
            _lines[path] = lines = File.Exists(full) ? File.ReadAllLines(full) : [];
        }
        return lines;
    }
}
