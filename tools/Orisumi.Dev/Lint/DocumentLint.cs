using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Orisumi.Dev;

internal sealed record DocumentLink(string Target, int Index);

internal enum StatementLevel { Stance, Hypothesis, Inference }

internal sealed record Statement(string Page, string Name, StatementLevel Level, IReadOnlyList<string> Basis, int Line);

internal sealed record RequirementGraph(
    int Line,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<(string From, string To)> Edges,
    IReadOnlyList<string> Hypotheses,
    IReadOnlyDictionary<string, string> Clicks);

internal static partial class DocumentLint
{
    private static readonly Dictionary<string, StatementLevel> Levels = new(StringComparer.Ordinal)
    {
        ["立场"] = StatementLevel.Stance,
        ["假设"] = StatementLevel.Hypothesis,
        ["推论"] = StatementLevel.Inference,
    };

    private static readonly Dictionary<StatementLevel, string> LevelNames = Levels.ToDictionary(item => item.Value, item => item.Key);

    public static bool Covers(string path) => SourceLanguages.Of(path) == "Markdown" && !ProjectLayout.IsScratch(path);

    public static IReadOnlyList<LintFinding> Scan(IReadOnlyDictionary<string, string> documents, Func<string, bool> exists)
    {
        var anchors = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        HashSet<string> AnchorsOf(string path) =>
            anchors.TryGetValue(path, out var known) ? known : anchors[path] = Anchors(documents.TryGetValue(path, out var text) ? text : "");
        var findings = new List<LintFinding>();
        foreach (var (path, text) in documents)
        {
            foreach (var link in Links(text).Concat(MermaidLinks(text)))
            {
                if (Check(path, link.Target, documents.ContainsKey, exists, AnchorsOf) is { } problem)
                    findings.Add(new LintFinding(problem.Rule, path, LineAt(text, link.Index), problem.Message));
            }
            findings.AddRange(JsonBlocks(path, text));
        }
        var statements = documents.SelectMany(document => Statements(document.Key, document.Value)).ToArray();
        findings.AddRange(RequirementFindings(statements, documents));
        return findings;
    }

    private static (string Rule, string Message)? Check(
        string from, string target, Func<string, bool> isDocument, Func<string, bool> exists, Func<string, HashSet<string>> anchors)
    {
        var hash = target.IndexOf('#', StringComparison.Ordinal);
        var path = hash < 0 ? target : target[..hash];
        var anchor = hash < 0 ? "" : Uri.UnescapeDataString(target[(hash + 1)..]);
        if (Scheme().IsMatch(path)) return null;
        if (path.StartsWith('/')) return ("link", $"{target} is a site path; link to the file by its relative path");
        var file = path.Length == 0 ? from : Normalize(Path.GetDirectoryName(from)!, Uri.UnescapeDataString(path));
        if (file is null || !(isDocument(file) || exists(file))) return ("link", $"{target} does not exist");
        if (anchor.Length > 0 && Covers(file) && !anchors(file).Contains(anchor)) return ("anchor", $"{target} has no anchor #{anchor}");
        return null;
    }

    private static string? Normalize(string directory, string relative)
    {
        var parts = new List<string>(directory.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var part in relative.Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) return null;
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }
        return string.Join('/', parts);
    }

    internal static string Slugify(string heading) =>
        SlugNoise().Replace(heading.Trim().ToLowerInvariant(), "").Replace(' ', '-');

    internal static int LineAt(string text, int index) => text.AsSpan(0, Math.Min(index, text.Length)).Count('\n') + 1;

    internal static string MaskCode(string text)
    {
        static string Blank(Match match) => Regex.Replace(match.Value, "[^\n]", " ");
        return InlineCode().Replace(Fence().Replace(text, Blank), Blank);
    }

    internal static HashSet<string> Anchors(string text) =>
        [.. Heading().Matches(MaskCode(text)).Select(match => match.Groups[1].Value)
            .Select(heading => ExplicitAnchor().Match(heading) is { Success: true } id ? id.Groups[1].Value : Slugify(heading))];

    internal static IReadOnlyList<DocumentLink> Links(string text) =>
        [.. Link().Matches(MaskCode(text)).Select(match => new DocumentLink(match.Groups[2].Value, match.Index))];

    internal static IReadOnlyList<DocumentLink> MermaidLinks(string text) =>
        [.. Mermaid().Matches(text).SelectMany(block =>
        {
            var start = block.Index + block.Value.IndexOf('\n', StringComparison.Ordinal) + 1;
            return Click().Matches(block.Groups[1].Value).Select(match => new DocumentLink(match.Groups[3].Value, start + match.Index));
        })];

    internal static IEnumerable<LintFinding> JsonBlocks(string path, string text)
    {
        foreach (Match block in JsonFence().Matches(text))
        {
            string? error = null;
            try
            {
                using var _ = JsonDocument.Parse(block.Groups[1].Value);
            }
            catch (JsonException exception)
            {
                error = exception.Message;
            }
            if (error is not null) yield return new LintFinding("json", path, LineAt(text, block.Index), error);
        }
    }

    internal static IReadOnlyList<Statement> Statements(string page, string text)
    {
        var statements = new List<Statement>();
        StatementLevel? level = null;
        (string Name, StatementLevel Level, List<string> Basis, int Line)? current = null;
        var lineNumber = 0;
        void Flush()
        {
            if (current is { } open) statements.Add(new Statement(page, open.Name, open.Level, open.Basis, open.Line));
            current = null;
        }
        foreach (var line in MaskCode(text).Split('\n'))
        {
            lineNumber++;
            if (Section().Match(line) is { Success: true } section)
            {
                Flush();
                level = Levels.TryGetValue(section.Groups[1].Value, out var found) ? found : null;
            }
            else if (level is { } open && Subsection().Match(line) is { Success: true } heading)
            {
                Flush();
                current = (ExplicitAnchorSuffix().Replace(heading.Groups[1].Value, ""), open, [], lineNumber);
            }
            else if (current is { } statement && line.StartsWith("依据：", StringComparison.Ordinal))
            {
                statement.Basis.AddRange(Links(line).Select(link =>
                    link.Target.IndexOf('#', StringComparison.Ordinal) is var hash and >= 0
                        ? Uri.UnescapeDataString(link.Target[(hash + 1)..]) : ""));
            }
        }
        Flush();
        return statements;
    }

    internal static IReadOnlyList<RequirementGraph> Graphs(string text) =>
        [.. Mermaid().Matches(text).Select(block =>
        {
            var body = block.Groups[1].Value;
            return new RequirementGraph(
                LineAt(text, block.Index),
                Node().Matches(body).GroupBy(match => match.Groups[1].Value).ToDictionary(group => group.Key, group => group.First().Groups[2].Value),
                [.. Edge().Matches(body).Select(match => (match.Groups[1].Value, match.Groups[2].Value))],
                [.. HypothesisClass().Matches(body).SelectMany(match => match.Groups[1].Value.Split(','))],
                Click().Matches(body).GroupBy(match => match.Groups[1].Value).ToDictionary(group => group.Key, group => group.First().Groups[3].Value));
        })];

    internal static IReadOnlyList<LintFinding> RequirementFindings(
        IReadOnlyList<Statement> statements, IReadOnlyDictionary<string, string> documents)
    {
        var byName = new Dictionary<string, Statement>(StringComparer.Ordinal);
        var findings = new List<LintFinding>();
        foreach (var statement in statements)
        {
            if (byName.TryGetValue(statement.Name, out var existing))
                findings.Add(Requirement(statement, $"{statement.Name} is already a statement in {existing.Page}"));
            else byName[statement.Name] = statement;
        }
        findings.AddRange(statements.SelectMany(statement => BasisFindings(statement, byName)));
        findings.AddRange(Cycles(byName).Select(cycle => Requirement(byName[cycle[0]], $"依据 forms a cycle: {string.Join(" → ", cycle)}")));
        foreach (var page in statements.Select(statement => statement.Page).Distinct())
        {
            var own = statements.Where(statement => statement.Page == page).ToArray();
            findings.AddRange(Graphs(documents[page]).SelectMany(graph => GraphFindings(page, graph, own, byName)));
        }
        return findings;
    }

    private static LintFinding Requirement(Statement statement, string message) => new("requirements", statement.Page, statement.Line, message);

    private static IEnumerable<LintFinding> BasisFindings(Statement statement, Dictionary<string, Statement> byName)
    {
        if (statement.Level == StatementLevel.Inference && statement.Basis.Count == 0)
            yield return Requirement(statement, $"推论 {statement.Name} needs 依据");
        if (statement.Level != StatementLevel.Inference && statement.Basis.Count > 0)
            yield return Requirement(statement, $"{LevelNames[statement.Level]} {statement.Name} is a starting point and takes no 依据");
        foreach (var basis in statement.Basis.Where(basis => !byName.ContainsKey(basis)))
            yield return Requirement(statement, $"依据 of {statement.Name} is not a statement: {basis}");
    }

    private static List<string[]> Cycles(Dictionary<string, Statement> byName)
    {
        var cycles = new List<string[]>();
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string name, List<string> path)
        {
            if (done.Contains(name)) return;
            if (!visiting.Add(name))
            {
                cycles.Add([.. path.Skip(path.IndexOf(name)), name]);
                return;
            }
            path.Add(name);
            foreach (var basis in byName.TryGetValue(name, out var statement) ? statement.Basis : []) Visit(basis, path);
            path.RemoveAt(path.Count - 1);
            visiting.Remove(name);
            done.Add(name);
        }
        foreach (var name in byName.Keys) Visit(name, []);
        return cycles;
    }

    private static IEnumerable<LintFinding> GraphFindings(
        string page, RequirementGraph graph, IReadOnlyList<Statement> own, Dictionary<string, Statement> byName)
    {
        string Label(string id) => graph.Labels.TryGetValue(id, out var label) ? label : id;
        var nodes = graph.Labels.Values.ToHashSet(StringComparer.Ordinal);
        var drawn = graph.Edges.Select(edge => $"{Label(edge.From)} → {Label(edge.To)}").ToHashSet(StringComparer.Ordinal);
        var stated = own.SelectMany(statement => statement.Basis.Select(basis => $"{basis} → {statement.Name}")).ToHashSet(StringComparer.Ordinal);
        var dashed = graph.Hypotheses.Select(Label).ToHashSet(StringComparer.Ordinal);
        var messages = own.Select(statement => statement.Name).Where(name => !nodes.Contains(name)).Select(name => $"{name} is missing from the graph")
            .Concat(nodes.Where(name => !byName.ContainsKey(name)).Select(name => $"{name} in the graph is not a statement"))
            .Concat(stated.Where(edge => !drawn.Contains(edge)).Select(edge => $"the graph lacks the edge {edge}"))
            .Concat(drawn.Where(edge => !stated.Contains(edge)).Select(edge => $"the edge {edge} has no matching 依据"))
            .Concat(own.Where(statement => statement.Level == StatementLevel.Hypothesis && !dashed.Contains(statement.Name))
                .Select(statement => $"假设 {statement.Name} needs class hypothesis"))
            .Concat(dashed.Where(name => !byName.TryGetValue(name, out var statement) || statement.Level != StatementLevel.Hypothesis)
                .Select(name => $"{name} has class hypothesis but is not a 假设"))
            .Concat(graph.Labels.Where(label => !graph.Clicks.TryGetValue(label.Key, out var href) || href != $"#{label.Value}")
                .Select(label => $"{label.Value} needs click {label.Key} href \"#{label.Value}\""));
        return messages.Select(message => new LintFinding("requirements", page, graph.Line, message));
    }

    public static Dictionary<string, string> Read(RepositoryContext repository, IEnumerable<string> paths)
    {
        var documents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in paths.Where(Covers))
        {
            var full = repository.Path(path);
            if (File.Exists(full)) documents[path] = File.ReadAllText(full, Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        return documents;
    }

    [GeneratedRegex(@"^[a-z]+:", RegexOptions.IgnoreCase)]
    private static partial Regex Scheme();

    [GeneratedRegex(@"[^\p{L}\p{M}\p{N}\p{Pc}\- ]")]
    private static partial Regex SlugNoise();

    [GeneratedRegex(@"^```[^\n]*\n[\s\S]*?^```[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex Fence();

    [GeneratedRegex(@"`[^`\n]*`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"^#{1,6}[ \t]+(.+?)[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex Heading();

    [GeneratedRegex(@"\[#([^\]]+)\]$")]
    private static partial Regex ExplicitAnchor();

    [GeneratedRegex(@"[ \t]*\[#[^\]]+\]$")]
    private static partial Regex ExplicitAnchorSuffix();

    [GeneratedRegex(@"\[([^\]\n]*)\]\(([^)\s]+)\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"^```mermaid(?:[ \t][^\n]*)?\n([\s\S]*?)^```[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex Mermaid();

    [GeneratedRegex(@"^```json(?:[ \t][^\n]*)?\n([\s\S]*?)^```[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex JsonFence();

    [GeneratedRegex(@"^[ \t]*click[ \t]+(\S+)[ \t]+href[ \t]+([""'])(.*?)\2", RegexOptions.Multiline)]
    private static partial Regex Click();

    [GeneratedRegex(@"^##[ \t]+(.+?)[ \t]*$")]
    private static partial Regex Section();

    [GeneratedRegex(@"^###[ \t]+(.+?)[ \t]*$")]
    private static partial Regex Subsection();

    [GeneratedRegex(@"^[ \t]*([\w-]+)[ \t]*\[[ \t]*""?([^""\]]+?)""?[ \t]*\][ \t]*$", RegexOptions.Multiline)]
    private static partial Regex Node();

    [GeneratedRegex(@"^[ \t]*([\w-]+)[ \t]*-->[ \t]*([\w-]+)[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex Edge();

    [GeneratedRegex(@"^[ \t]*class[ \t]+([\w,-]+)[ \t]+hypothesis[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex HypothesisClass();
}
