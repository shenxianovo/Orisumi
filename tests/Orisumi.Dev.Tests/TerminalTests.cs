using System.Text.Json;
using Orisumi.Dev;

namespace Orisumi.Dev.Tests;

public sealed class TerminalTests
{
    [Test]
    public async Task RedirectedOutputHasNoEscapeCodesOrTrailingSpaces()
    {
        using var writer = new StringWriter();
        var console = Terminal.Create(writer);
        Terminal.Rows(console, [["a", "[green]long value[/]"], ["longer name", "b"]]);
        Terminal.Fields(console, [("Label", "[red]value[/]")]);

        var text = writer.ToString();
        await Assert.That(text).DoesNotContain("\u001b");
        await Assert.That(text.Split('\n').Any(line => line.TrimEnd('\r').EndsWith(' '))).IsFalse();
        await Assert.That(text).Contains("  longer name   b");
    }

    [Test]
    [Arguments("verify changed")]
    [Arguments("review check")]
    public async Task RunSummaryShowsTheCommandThatStartedTheRunAndItsFailedChecks(string title)
    {
        var directory = Path.Combine(Path.GetTempPath(), "orisumi-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var started = DateTimeOffset.UtcNow;
            File.WriteAllText(Path.Combine(directory, RunFiles.Manifest), JsonSerializer.Serialize(new
            {
                title, exitCode = 1, startedAt = started, completedAt = started.AddSeconds(95),
            }));
            File.WriteAllText(Path.Combine(directory, RunFiles.Verification), JsonSerializer.Serialize(new
            {
                checks = new object[] { new { name = "dotnet build", exitCode = 0 }, new { name = "quality", exitCode = 1 } },
            }));

            var summary = RunStore.Summarize(new RunRecord("run", directory, started));

            await Assert.That(summary.Title).IsEqualTo(title);
            await Assert.That(summary.ExitCode).IsEqualTo(1);
            await Assert.That(summary.Duration).IsEqualTo(TimeSpan.FromSeconds(95));
            await Assert.That(string.Join(",", summary.Failed)).IsEqualTo("quality");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public async Task RunsWithoutManifestAreUnfinished()
    {
        var directory = Path.Combine(Path.GetTempPath(), "orisumi-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var summary = RunStore.Summarize(new RunRecord("20261030T050639Z-verify-changed-abc", directory, DateTimeOffset.UtcNow));
            await Assert.That(summary.ExitCode).IsNull();
            await Assert.That(summary.Title).IsEqualTo("20261030T050639Z-verify-changed-abc");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    [Arguments("Orisumi.Dev/RunStore`0/Summarize`0(:RunRecord)", "RunStore.Summarize")]
    [Arguments("FunctionDeclaration:useDay/FunctionDeclaration:experienceOf", "useDay.experienceOf")]
    [Arguments("FunctionDeclaration:EntityPage", "EntityPage")]
    public async Task AnalyzerSymbolsKeepTheTypeAndFunctionName(string symbol, string expected) =>
        await Assert.That(QualityCheck.ShortSymbol(symbol)).IsEqualTo(expected);
}
