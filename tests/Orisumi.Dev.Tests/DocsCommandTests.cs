using Orisumi.Dev;

namespace Orisumi.Dev.Tests;

public sealed class DocsCommandTests
{
    [Test]
    public async Task DevInstallsDependenciesThenRunsTheServer()
    {
        var runner = new PnpmRunner(installExitCode: 0);
        var command = new DocsCommand(new RepositoryContext("/repo"), runner, TextWriter.Null);
        await Assert.That(await command.DevAsync(CancellationToken.None)).IsEqualTo(0);
        await Assert.That(string.Join(" | ", runner.Calls)).IsEqualTo(
            "pnpm --dir /repo/src/Docs/Orisumi.Docs install --frozen-lockfile | pnpm --dir /repo/src/Docs/Orisumi.Docs dev");
    }

    [Test]
    public async Task FailedInstallDoesNotStartTheServer()
    {
        var runner = new PnpmRunner(installExitCode: 1);
        var command = new DocsCommand(new RepositoryContext("/repo"), runner, TextWriter.Null);
        await Assert.That(await command.DevAsync(CancellationToken.None)).IsEqualTo(1);
        await Assert.That(runner.Calls.Count).IsEqualTo(1);
    }

    private sealed class PnpmRunner(int installExitCode) : IProcessRunner
    {
        public List<string> Calls { get; } = [];
        public Task<ProcessResult> CaptureAsync(ProcessCommand command, CancellationToken cancellationToken)
        {
            Calls.Add(command.ToString());
            return Task.FromResult(new ProcessResult(command.Arguments.Contains("install") ? installExitCode : 0, "", ""));
        }
    }
}
