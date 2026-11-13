namespace Orisumi.Cli.Dev.Loc;

internal static class SourceLanguages
{
    private static readonly IReadOnlyDictionary<string, string> Extensions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".cs"] = "C#",
            [".css"] = "CSS",
            [".js"] = "JavaScript",
            [".jsx"] = "JavaScript",
            [".mjs"] = "JavaScript",
            [".cjs"] = "JavaScript",
            [".ts"] = "TypeScript",
            [".tsx"] = "TypeScript",
            [".mts"] = "TypeScript",
            [".cts"] = "TypeScript",
            [".swift"] = "Swift",
            [".sh"] = "Shell",
            [".ps1"] = "PowerShell",
            [".py"] = "Python",
            [".yaml"] = "YAML",
            [".yml"] = "YAML",
            [".csproj"] = "MSBuild",
            [".props"] = "MSBuild",
            [".targets"] = "MSBuild",
            [".slnx"] = "XML",
            [".md"] = "Markdown",
            [".mdx"] = "Markdown",
            [".json"] = "JSON",
        };

    private static readonly string[] BuildLanguages = ["MSBuild", "XML", "Dockerfile", "YAML", "JSON"];

    public static string? Of(string path)
    {
        var name = Path.GetFileName(path);
        return name == "Dockerfile" ? "Dockerfile" : Extensions.GetValueOrDefault(Path.GetExtension(name));
    }

    public static bool IsBuild(string path, string language) =>
        BuildLanguages.Contains(language, StringComparer.Ordinal)
        || Path.GetFileName(path).Contains(".config.", StringComparison.Ordinal);

    public static bool IsGenerated(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("ModelSnapshot.cs", StringComparison.OrdinalIgnoreCase)
            || name is "next-env.d.ts" or "pnpm-lock.yaml"
            || name.EndsWith("-lock.json", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTest(string path)
    {
        var segments = path.Split('/');
        if (segments[..^1].Any(segment => segment.Equals("tests", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("test", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("__tests__", StringComparison.OrdinalIgnoreCase)
            || segment.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        var parts = segments[^1].Split('.');
        return parts.Length > 2 && parts[1..^1].Any(part => part is "test" or "spec");
    }
}

internal static class SourceLineCounter
{
    private static readonly IReadOnlyDictionary<string, string[]> LineComments =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["C#"] = ["//"],
            ["Dockerfile"] = ["#"],
            ["JavaScript"] = ["//"],
            ["PowerShell"] = ["#"],
            ["Python"] = ["#"],
            ["Shell"] = ["#"],
            ["Swift"] = ["//"],
            ["TypeScript"] = ["//"],
            ["YAML"] = ["#"],
        };

    private static readonly IReadOnlyDictionary<string, (string Start, string End)[]> BlockComments =
        new Dictionary<string, (string, string)[]>(StringComparer.Ordinal)
        {
            ["C#"] = [("/*", "*/")],
            ["CSS"] = [("/*", "*/")],
            ["JavaScript"] = [("/*", "*/")],
            ["Swift"] = [("/*", "*/")],
            ["TypeScript"] = [("/*", "*/")],
            ["Markdown"] = [("<!--", "-->")],
            ["MSBuild"] = [("<!--", "-->")],
            ["PowerShell"] = [("<#", "#>")],
            ["XML"] = [("<!--", "-->")],
        };

    public static int Count(string text, string language)
    {
        var lineTokens = LineComments.GetValueOrDefault(language) ?? [];
        var blockPairs = BlockComments.GetValueOrDefault(language) ?? [];
        string? activeBlockEnd = null;
        MultilineString? multiline = null;
        var effectiveLines = 0;
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (LineHasCode(line, language, lineTokens, blockPairs, ref activeBlockEnd, ref multiline)) effectiveLines++;
        }
        return effectiveLines;
    }

    private static bool LineHasCode(
        string line,
        string language,
        IReadOnlyList<string> lineTokens,
        IReadOnlyList<(string Start, string End)> blockPairs,
        ref string? activeBlockEnd,
        ref MultilineString? multiline)
    {
        var position = 0;
        var hasCode = false;
        char? quote = null;
        var escaped = false;
        while (position < line.Length)
        {
            if (ConsumeActiveBlock(line, ref position, ref activeBlockEnd)) continue;
            if (activeBlockEnd is not null) break;
            if (ConsumeMultiline(line, ref position, ref multiline, ref hasCode)) continue;
            if (quote is not null && ConsumeQuotedCharacter(line, ref position, ref quote, ref escaped, ref hasCode)) continue;
            if (StartsLineComment(line, position, lineTokens)) break;
            if (StartBlock(line, ref position, blockPairs, ref activeBlockEnd)) continue;
            if (StartMultiline(line, language, ref position, ref multiline))
            {
                hasCode = true;
                continue;
            }
            if (ConsumeQuotedCharacter(line, ref position, ref quote, ref escaped, ref hasCode)) continue;
            if (!char.IsWhiteSpace(line[position])) hasCode = true;
            position++;
        }
        return hasCode;
    }

    private sealed record MultilineString(string End, bool Escapes = false, bool Verbatim = false);

    private static bool StartMultiline(string line, string language, ref int position, ref MultilineString? literal)
    {
        var rest = line.AsSpan(position);
        if (language == "C#" && (rest.StartsWith("@\"") || rest.StartsWith("@$\"")))
        {
            literal = new("\"", Verbatim: true);
            position += rest.StartsWith("@$\"") ? 3 : 2;
            return true;
        }
        if (language is "JavaScript" or "TypeScript" && rest[0] == '`')
        {
            literal = new("`", Escapes: true);
            position++;
            return true;
        }
        if (language is "C#" or "Swift" or "Python" &&
            (rest.StartsWith("\"\"\"") || language == "Python" && rest.StartsWith("'''")))
        {
            var width = 3;
            if (language == "C#")
                while (width < rest.Length && rest[width] == '"') width++;
            literal = new(new string(rest[0], width), Escapes: language != "C#");
            position += width;
            return true;
        }
        return false;
    }

    private static bool ConsumeMultiline(string line, ref int position, ref MultilineString? literal, ref bool hasCode)
    {
        if (literal is null) return false;
        hasCode |= !char.IsWhiteSpace(line[position]);
        if (literal.Escapes && line[position] == '\\')
            position = Math.Min(position + 2, line.Length);
        else if (literal.Verbatim && line.AsSpan(position).StartsWith("\"\""))
            position += 2;
        else if (line.AsSpan(position).StartsWith(literal.End, StringComparison.Ordinal))
        {
            position += literal.End.Length;
            literal = null;
        }
        else position++;
        return true;
    }

    private static bool ConsumeActiveBlock(string line, ref int position, ref string? activeBlockEnd)
    {
        if (activeBlockEnd is null) return false;
        var end = line.IndexOf(activeBlockEnd, position, StringComparison.Ordinal);
        if (end < 0) return false;
        position = end + activeBlockEnd.Length;
        activeBlockEnd = null;
        return true;
    }

    private static bool ConsumeQuotedCharacter(
        string line,
        ref int position,
        ref char? quote,
        ref bool escaped,
        ref bool hasCode)
    {
        var character = line[position];
        if (quote is null)
        {
            if (character is not ('"' or '\'' or '`')) return false;
            quote = character;
            hasCode = true;
            position++;
            return true;
        }
        if (!char.IsWhiteSpace(character)) hasCode = true;
        if (escaped) escaped = false;
        else if (character == '\\') escaped = true;
        else if (character == quote) quote = null;
        position++;
        return true;
    }

    private static bool StartsLineComment(string line, int position, IReadOnlyList<string> tokens) =>
        tokens.Any(token => line.AsSpan(position).StartsWith(token, StringComparison.Ordinal));

    private static bool StartBlock(
        string line,
        ref int position,
        IReadOnlyList<(string Start, string End)> pairs,
        ref string? activeBlockEnd)
    {
        foreach (var pair in pairs)
        {
            if (!line.AsSpan(position).StartsWith(pair.Start, StringComparison.Ordinal)) continue;
            activeBlockEnd = pair.End;
            position += pair.Start.Length;
            return true;
        }
        return false;
    }
}
