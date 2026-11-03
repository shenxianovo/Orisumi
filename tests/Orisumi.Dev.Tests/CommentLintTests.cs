using Orisumi.Dev;

namespace Orisumi.Dev.Tests;

public sealed class CommentLintTests
{
    [Test]
    public async Task CSharpCommentsOfEveryKindAreFoundAndStringsAreNot()
    {
        const string source = """
            /// <summary>doc</summary>
            class A
            {
                // line
                /* block */
                string url = "https://example.com // not a comment";
            }
            """;
        var lines = CommentLint.CSharpComments("A.cs", source).Select(finding => finding.Line);
        await Assert.That(string.Join(',', lines)).IsEqualTo("1,4,5");
    }

    [Test]
    [Arguments("tools/Orisumi.Dev/Program.cs", true)]
    [Arguments("src/Docs/Orisumi.Docs/components/mermaid.tsx", true)]
    [Arguments("tools/Orisumi.Dev/node-tools/comments.mjs", true)]
    [Arguments("src/Docs/Orisumi.Docs/app/global.css", false)]
    [Arguments("README.md", false)]
    public async Task OnlySourceFilesAreCovered(string path, bool covered) =>
        await Assert.That(CommentLint.Covers(path)).IsEqualTo(covered);
}
