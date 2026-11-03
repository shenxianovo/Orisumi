using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Orisumi.Dev;

internal sealed class CommentLint(RepositoryContext repository, IProcessRunner runner)
{
    private sealed record ScriptComment(string Path, int Line, string Text);

    private static readonly string[] Directives = ["/// <reference", "// @ts-", "/* @ts-", "#!"];

    public static bool Covers(string path) => SourceLanguages.Of(path) is "C#" or "TypeScript" or "JavaScript";

    public async Task<IReadOnlyList<LintFinding>> ScanAsync(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        var files = paths.Where(path => Covers(path) && File.Exists(repository.Path(path))).ToArray();
        var csharp = files.Where(path => SourceLanguages.Of(path) == "C#")
            .SelectMany(path => CSharpComments(path, File.ReadAllText(repository.Path(path))));
        var scripts = await ScriptCommentsAsync([.. files.Where(path => SourceLanguages.Of(path) != "C#")], cancellationToken);
        return [.. csharp.Concat(scripts).Where(finding => !Directives.Any(directive => finding.Message.StartsWith(directive, StringComparison.Ordinal)))];
    }

    internal static IEnumerable<LintFinding> CSharpComments(string path, string text) =>
        CSharpSyntaxTree.ParseText(text).GetRoot().DescendantTrivia(descendIntoTrivia: true)
            .Where(trivia => trivia.Kind() is SyntaxKind.SingleLineCommentTrivia or SyntaxKind.MultiLineCommentTrivia
                or SyntaxKind.SingleLineDocumentationCommentTrivia or SyntaxKind.MultiLineDocumentationCommentTrivia)
            .Select(trivia => new LintFinding("comment", path, trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                trivia.ToFullString().Trim()));

    private async Task<IEnumerable<LintFinding>> ScriptCommentsAsync(string[] files, CancellationToken cancellationToken)
    {
        if (files.Length == 0) return [];
        var tools = new NodeTools(repository, runner);
        await tools.EnsureAsync(cancellationToken);
        var list = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(list, JsonSerializer.Serialize(files), cancellationToken);
            var result = await runner.CaptureAsync(new("node", [tools.Script("comments.mjs"), repository.Root, list]), cancellationToken);
            if (result.ExitCode != 0) throw new InvalidOperationException($"Scanning script comments failed: {result.StdErr.Trim()}");
            return (JsonSerializer.Deserialize<ScriptComment[]>(result.StdOut, JsonOptions.Indented) ?? [])
                .Select(comment => new LintFinding("comment", comment.Path, comment.Line, comment.Text));
        }
        finally
        {
            File.Delete(list);
        }
    }
}
