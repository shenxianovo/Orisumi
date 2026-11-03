using Orisumi.Dev;

namespace Orisumi.Dev.Tests;

public sealed class DeveloperCliTests
{
    [Test]
    [Arguments("src/Docs/Orisumi.Docs/content/docs/start/index.mdx", "docs-check,docs-test,docs-types,docs-build")]
    [Arguments("AGENTS.md", "docs-check")]
    [Arguments("notes/README.md", "docs-check")]
    [Arguments("tools/Orisumi.Dev/README.md", "dotnet-build,developer-cli-tests,docs-check")]
    [Arguments("tools/Orisumi.Dev/DeveloperCli.cs", "lint,dotnet-build,developer-cli-tests")]
    [Arguments("tests/Orisumi.Dev.Tests/TerminalTests.cs", "lint,dotnet-build,developer-cli-tests")]
    [Arguments("src/Docs/Orisumi.Docs/components/mermaid.tsx", "lint,docs-check,docs-test,docs-types,docs-build")]
    [Arguments(".scratch/model/notes.md", "")]
    [Arguments("unclassified.config", "lint,dotnet-build,developer-cli-tests,docs-check,docs-test,docs-types,docs-build")]
    public async Task ChangedVerificationSelectsCurrentChecks(string path, string expected)
    {
        var runner = new RecordingRunner { ChangedPath = path };
        var plan = await VerificationPlanner.CreateAsync(new RepositoryContext(Path.GetTempPath()), runner,
            new VerificationRequest("changed", "HEAD", true, false), CancellationToken.None);
        await Assert.That(string.Join(',', plan.Steps.Select(step => step.Name))).IsEqualTo(expected);
    }

    private sealed class RecordingRunner : IProcessRunner
    {
        public string? ChangedPath { get; init; }

        public Task<ProcessResult> CaptureAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken)
            => Task.FromResult(new ProcessResult(0,
                fileName == "git" && arguments[0] == "diff" && ChangedPath is not null ? ChangedPath + '\0' : "", ""));
    }
}
