using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Orisumi.Dev;

internal sealed class Evidence(RepositoryContext repository, ProjectLayout layout, IReadOnlyList<string> files)
{
    private const int MaxReferences = 15;

    private readonly Dictionary<string, string[]> _lines = new(StringComparer.Ordinal);

    public string Unused(DeclaredException exception)
    {
        var text = Heading(exception.Rule, exception.Path, null, exception.Symbol);
        if (layout.Owner(exception.Path) is not { } project) return text.ToString().TrimEnd();
        var term = exception.Rule == "knip/files" ? Path.ChangeExtension(exception.Path[project.Directory.Length..], null)! : exception.Symbol;
        var pattern = new Regex($"(?<![\\w@-]){Regex.Escape(term)}(?![\\w-])", RegexOptions.CultureInvariant);
        var exceptions = project.Directory + UnusedLint.ExceptionsFile;
        var hits = files
            .Where(path => project.Contains(path) && path != project.Manifest && path != exceptions && !SourceLanguages.IsGenerated(path))
            .SelectMany(path => Lines(path).Select((line, index) => (Path: path, Line: index + 1, Text: line)))
            .Where(hit => pattern.IsMatch(hit.Text))
            .ToArray();
        text.AppendLine().AppendLine(ReferencesHeading(term, hits.Length));
        Append(text, hits.Select(hit => $"{hit.Path}:{hit.Line}: {Clip(hit.Text.Trim())}"));
        if (exception.Rule is "knip/dependencies" or "knip/devDependencies")
        {
            var key = new Regex($"^\\s*'?{Regex.Escape(term)}(@[^:']*)?'?:", RegexOptions.CultureInvariant);
            text.AppendLine().AppendLine("锁文件中把它作为依赖项的位置：");
            Append(text, files.Where(path => project.Contains(path) && SourceLanguages.IsGenerated(path)).SelectMany(path => Chains(Lines(path), key)));
        }
        return text.ToString().TrimEnd();
    }

    internal static string ReferencesHeading(string term, int count) =>
        string.Create(CultureInfo.InvariantCulture, $"项目内对「{term}」的引用（{count} 处，不含项目清单、锁文件和例外声明）：");

    private StringBuilder Heading(string rule, string path, int? line, string symbol) => new StringBuilder()
        .AppendLine(CultureInfo.InvariantCulture, $"发现：{rule}")
        .AppendLine(CultureInfo.InvariantCulture, $"位置：{path}{(line is { } number ? $":{number}" : "")}")
        .AppendLine(CultureInfo.InvariantCulture, $"对象：{symbol}")
        .AppendLine(CultureInfo.InvariantCulture, $"所属项目：{layout.Owner(path)?.Name ?? "无"}");

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
