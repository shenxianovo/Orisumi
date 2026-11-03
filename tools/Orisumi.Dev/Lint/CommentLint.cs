using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Orisumi.Dev;

internal sealed record CommentFinding(string Path, int Line, string Text);

internal sealed class CommentLint(RepositoryContext repository, IProcessRunner runner)
{
    private static readonly string[] ScriptExtensions = [".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs"];

    private static readonly string[] Directives = ["/// <reference", "// @ts-", "/* @ts-", "#!"];

    public static bool Covers(string path) =>
        path.EndsWith(".cs", StringComparison.Ordinal) || ScriptExtensions.Any(extension => path.EndsWith(extension, StringComparison.Ordinal));

    public async Task<IReadOnlyList<CommentFinding>> ScanAsync(CancellationToken cancellationToken)
    {
        var files = (await ReviewSelection.WorkspaceFilesAsync(runner, cancellationToken))
            .Where(path => Covers(path) && File.Exists(repository.Path(path)))
            .ToArray();
        var csharp = files.Where(path => path.EndsWith(".cs", StringComparison.Ordinal))
            .SelectMany(path => CSharpComments(path, File.ReadAllText(repository.Path(path))));
        var scripts = await ScriptCommentsAsync(files.Where(path => !path.EndsWith(".cs", StringComparison.Ordinal)).ToArray(), cancellationToken);
        return csharp.Concat(scripts)
            .Where(finding => !Directives.Any(directive => finding.Text.StartsWith(directive, StringComparison.Ordinal)))
            .OrderBy(finding => finding.Path, StringComparer.Ordinal).ThenBy(finding => finding.Line)
            .ToArray();
    }

    internal static IEnumerable<CommentFinding> CSharpComments(string path, string text) =>
        CSharpSyntaxTree.ParseText(text).GetRoot().DescendantTrivia(descendIntoTrivia: true)
            .Where(trivia => trivia.Kind() is SyntaxKind.SingleLineCommentTrivia or SyntaxKind.MultiLineCommentTrivia
                or SyntaxKind.SingleLineDocumentationCommentTrivia or SyntaxKind.MultiLineDocumentationCommentTrivia)
            .Select(trivia => new CommentFinding(path, trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                trivia.ToFullString().Trim()));

    private async Task<IReadOnlyList<CommentFinding>> ScriptCommentsAsync(string[] files, CancellationToken cancellationToken)
    {
        if (files.Length == 0) return [];
        var tools = new NodeTools(repository);
        await tools.EnsureAsync(cancellationToken);
        var list = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(list, JsonSerializer.Serialize(files), cancellationToken);
            var result = await runner.CaptureAsync("node", [tools.Script("comments.mjs"), repository.Root, list], null, cancellationToken);
            if (result.ExitCode != 0) throw new InvalidOperationException($"Scanning script comments failed: {result.StdErr.Trim()}");
            return JsonSerializer.Deserialize<CommentFinding[]>(result.StdOut, JsonOptions.Indented) ?? [];
        }
        finally
        {
            File.Delete(list);
        }
    }
}
