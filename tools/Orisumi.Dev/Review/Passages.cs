namespace Orisumi.Dev;

internal sealed record Passage(int Line, string Text);

internal static class Passages
{
    public static IReadOnlyList<Passage> Split(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var passages = new List<Passage>();
        var block = new List<(int Line, string Text)>();
        var index = SkipFrontmatter(lines);
        var fence = false;
        void Flush()
        {
            if (block.Count > 0 && !block.All(line => IsHeading(line.Text) || IsModuleLine(line.Text)))
                passages.Add(new Passage(block[0].Line, string.Join('\n', block.Select(line => line.Text))));
            block.Clear();
        }
        for (; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal) || line.TrimStart().StartsWith("~~~", StringComparison.Ordinal))
            {
                Flush();
                fence = !fence;
                continue;
            }
            if (fence) continue;
            if (line.Length == 0) Flush();
            else block.Add((index + 1, line));
        }
        Flush();
        return passages;
    }

    public static IReadOnlyList<Passage> Added(string? before, string after)
    {
        var known = Split(before ?? "").Select(passage => passage.Text).ToHashSet(StringComparer.Ordinal);
        return Split(after).Where(passage => !known.Contains(passage.Text)).ToArray();
    }

    private static int SkipFrontmatter(string[] lines)
    {
        if (lines.Length == 0 || lines[0].TrimEnd() != "---") return 0;
        for (var index = 1; index < lines.Length; index++)
        {
            if (lines[index].TrimEnd() == "---") return index + 1;
        }
        return 0;
    }

    private static bool IsHeading(string line) => line.StartsWith('#');

    private static bool IsModuleLine(string line) =>
        line.StartsWith("import ", StringComparison.Ordinal) || line.StartsWith("export ", StringComparison.Ordinal);
}
