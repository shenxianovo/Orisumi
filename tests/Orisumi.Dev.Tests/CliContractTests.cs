using System.Text.Json;
using Orisumi.Dev;

namespace Orisumi.Dev.Tests;

public sealed class CliContractTests
{
    [Test]
    [Arguments(0)]
    [Arguments(7)]
    [Arguments(130)]
    public async Task JsonExecutionReportsTheOutcomeWithoutProgressOnStdout(int exitCode)
    {
        using var repository = Changed();
        using var error = new StringWriter();
        var cli = new DeveloperCli(repository.Context, new FixedRunner(exitCode), repository.Output, error);
        await Assert.That(await cli.RunAsync(["verify", "--base", "HEAD", "--json"], CancellationToken.None)).IsEqualTo(exitCode);
        using var result = JsonDocument.Parse(repository.Output.ToString());
        await Assert.That(result.RootElement.GetProperty("exitCode").GetInt32()).IsEqualTo(exitCode);
        await Assert.That(Check(result, "dotnet build").GetProperty("exitCode").GetInt32()).IsEqualTo(exitCode);
        await Assert.That(error.ToString()).Contains("dotnet build");
    }

    [Test]
    [Arguments("")]
    [Arguments("docs")]
    [Arguments("analyze")]
    [Arguments("runs")]
    [Arguments("tool")]
    [Arguments("review")]
    [Arguments("config")]
    public async Task GroupsShowHelpWithoutPerformingOperations(string path)
    {
        using var repository = new TestRepository(withProjects: false);
        var runner = new FixedRunner(0);
        var cli = new DeveloperCli(repository.Context, runner, repository.Output, TextWriter.Null);
        await Assert.That(await cli.RunAsync(path.Split(' ', StringSplitOptions.RemoveEmptyEntries), CancellationToken.None)).IsEqualTo(0);
        await Assert.That(runner.Calls.Count).IsEqualTo(0);
        await Assert.That(repository.Output.ToString()).Contains("orisumi");
        await Assert.That(repository.Output.ToString()).DoesNotContain("Orisumi.Dev");
    }

    [Test]
    [Arguments("verify changed")]
    [Arguments("verify full")]
    [Arguments("quality --base HEAD")]
    [Arguments("loc")]
    [Arguments("install")]
    [Arguments("artifacts list")]
    [Arguments("verify checks")]
    [Arguments("verify --scope invalid")]
    [Arguments("verify --scope all --base HEAD")]
    [Arguments("verify --scope all --quality")]
    public async Task InvalidCommandsDoNotPerformOperations(string arguments)
    {
        using var repository = new TestRepository(withProjects: false);
        var runner = new FixedRunner(0);
        var cli = new DeveloperCli(repository.Context, runner, repository.Output, TextWriter.Null);
        await Assert.That(await cli.RunAsync(arguments.Split(' '), CancellationToken.None)).IsEqualTo(2);
        await Assert.That(runner.Calls.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("changed", true)]
    [Arguments("all", false)]
    public async Task PlansDoNotExecuteChecksOrCreateEvidence(string scope, bool quality)
    {
        using var repository = Changed();
        var runner = new FixedRunner(0);
        var cli = new DeveloperCli(repository.Context, runner, repository.Output, TextWriter.Null);
        string[] selection = quality ? ["--base", "HEAD", "--quality"] : [];
        await Assert.That(await cli.RunAsync(["verify", "--scope", scope, .. selection, "--plan", "--json"], CancellationToken.None)).IsEqualTo(0);
        using var result = JsonDocument.Parse(repository.Output.ToString());
        await Assert.That(result.RootElement.GetProperty("scope").GetString()).IsEqualTo(scope);
        await Assert.That(Names(result).Contains("quality")).IsEqualTo(quality);
        await Assert.That(runner.Calls.Count).IsEqualTo(0);
        await Assert.That(new RunStore(repository.Context).List().Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task DefaultChangedScopeOnlyInfersHeadForDirtyWorktrees(bool dirty)
    {
        using var repository = dirty ? Changed() : new TestRepository();
        var cli = new DeveloperCli(repository.Context, new FixedRunner(0), repository.Output, TextWriter.Null);
        await Assert.That(await cli.RunAsync(["verify", "--plan", "--json"], CancellationToken.None)).IsEqualTo(dirty ? 0 : 2);
        if (!dirty) return;
        using var result = JsonDocument.Parse(repository.Output.ToString());
        await Assert.That(result.RootElement.GetProperty("base").GetString()).IsEqualTo("HEAD");
    }

    [Test]
    public async Task AnIncompleteQualityScanFailsTheRunWithoutHidingPassingChecks()
    {
        using var repository = Changed();
        var cli = new DeveloperCli(repository.Context, new FixedRunner(0), repository.Output, TextWriter.Null);
        await Assert.That(await cli.RunAsync(["verify", "--base", "HEAD", "--quality", "--json"], CancellationToken.None)).IsEqualTo(1);
        using var result = JsonDocument.Parse(repository.Output.ToString());
        await Assert.That(Check(result, "dotnet build").GetProperty("exitCode").GetInt32()).IsEqualTo(0);
        await Assert.That(Check(result, "quality").GetProperty("report").GetProperty("completed").GetBoolean()).IsFalse();
        var run = new RunStore(repository.Context).List().Single();
        await Assert.That(RunStore.Summarize(run).Failed).IsEquivalentTo(["quality"]);
    }

    [Test]
    public async Task CancellationProducesAJsonResultAndPreservesPartialEvidence()
    {
        using var repository = Changed();
        var cli = new DeveloperCli(repository.Context, new FixedRunner(0) { Cancel = true }, repository.Output, TextWriter.Null);
        await Assert.That(async () => await cli.RunAsync(["verify", "--base", "HEAD", "--json"], CancellationToken.None))
            .Throws<OperationCanceledException>();
        using var result = JsonDocument.Parse(repository.Output.ToString());
        await Assert.That(result.RootElement.GetProperty("exitCode").GetInt32()).IsEqualTo(130);
        var run = new RunStore(repository.Context).List().Single();
        await Assert.That(File.ReadAllText(Path.Combine(run.Directory, "dotnet-build.log"))).IsEqualTo("partial output");
        await Assert.That(RunStore.Summarize(run).ExitCode).IsEqualTo(130);
    }

    private static TestRepository Changed()
    {
        var repository = new TestRepository();
        repository.Write("tools/Example/Example.cs", "class Example { int Value => 1; }");
        return repository;
    }

    private static JsonElement Check(JsonDocument result, string name) =>
        result.RootElement.GetProperty("checks").EnumerateArray().Single(check => check.GetProperty("name").GetString() == name);

    private static string[] Names(JsonDocument result) =>
        [.. result.RootElement.GetProperty("checks").EnumerateArray().Select(check => check.GetProperty("name").GetString()!)];
}
