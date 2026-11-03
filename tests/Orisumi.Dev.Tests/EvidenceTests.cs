using System.Text.Json;
using Orisumi.Dev;

namespace Orisumi.Dev.Tests;

public sealed class EvidenceTests
{
    [Test]
    public async Task ACancelledProcessKeepsItsPartialLogAndTheRunRecordsTheCancellation()
    {
        using var repository = new TestRepository(withProjects: false);
        var check = new ProcessCheck("dotnet build", new("dotnet", ["build"]), new FixedRunner(0) { Cancel = true }, repository.Context);
        await Assert.That(async () => await Run.ExecuteAsync(repository.Context, "verify all", async run => (await check.RunAsync(run, CancellationToken.None)).ExitCode))
            .Throws<OperationCanceledException>();
        var run = new RunStore(repository.Context).List().Single();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(run.Directory, "dotnet-build.log"))).IsEqualTo("partial output");
        var summary = RunStore.Summarize(run);
        await Assert.That(summary.ExitCode).IsEqualTo(130);
        await Assert.That(summary.Title).IsEqualTo("verify all");
    }

    [Test]
    [Arguments("0,0", 0)]
    [Arguments("0,7,1", 7)]
    [Arguments("1,130", 130)]
    public async Task CancellationOutranksFailuresAndTheFirstFailureOutranksLaterOnes(string exitCodes, int expected)
    {
        var results = exitCodes.Split(',').Select(code => new CheckRecord("check", int.Parse(code, System.Globalization.CultureInfo.InvariantCulture), null, null)).ToArray();
        await Assert.That(VerificationCommand.ExitCode(results)).IsEqualTo(expected);
    }

    [Test]
    public async Task PruningKeepsTheUnionAndPreviewPreservesFiles()
    {
        using var repository = new TestRepository(withProjects: false);
        var store = new RunStore(repository.Context);
        for (var i = 0; i < 18; i++)
        {
            var run = store.Create($"run {i}");
            var age = i < 2 ? TimeSpan.FromHours(i + 1)
                : i < 12 ? TimeSpan.FromDays(3) + TimeSpan.FromHours(i)
                : TimeSpan.FromDays(6) + TimeSpan.FromHours(i);
            Directory.SetCreationTimeUtc(run.Directory, DateTime.UtcNow - age);
            await File.WriteAllTextAsync(run.PathOf(RunFiles.Manifest),
                JsonSerializer.Serialize(new { exitCode = i >= 12 ? (i % 2 == 0 ? 1 : 130) : 0 }));
        }
        await Assert.That(store.SelectForPruning(RetentionPolicy.Default).Candidates.Count).IsEqualTo(3);
        var cli = new DeveloperCli(repository.Context, new FixedRunner(0), TextWriter.Null, TextWriter.Null);
        await Assert.That(await cli.RunAsync(["runs", "prune"], CancellationToken.None)).IsEqualTo(0);
        await Assert.That(store.List().Count).IsEqualTo(18);
        await Assert.That(await cli.RunAsync(["runs", "prune", "--apply"], CancellationToken.None)).IsEqualTo(0);
        await Assert.That(store.List().Count).IsEqualTo(15);
    }
}
