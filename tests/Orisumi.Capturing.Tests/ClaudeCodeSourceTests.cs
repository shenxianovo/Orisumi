using Orisumi.Raw;

namespace Orisumi.Capturing.Tests;

public class ClaudeCodeSourceTests
{
    [Test]
    public async Task CapturesBothRootsWithOriginalBytesAndPaths()
    {
        using var sample = new CaptureSample();
        sample.File("logs/project/session.jsonl", "{\"type\":\"user\"}\n半行");
        sample.File("logs/project/session/subagents/agent.jsonl", "child\n");
        sample.File("logs/shared.json", "project metadata");
        sample.File("indexes/shared.json", "desktop metadata");
        var attachment = sample.File("logs/project/session/tool-results/image.png", "");
        File.WriteAllBytes(attachment, [0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF]);
        var projects = Path.Combine(sample.SourceDirectory, "logs");
        var desktop = Path.Combine(sample.SourceDirectory, "indexes");
        var source = ClaudeCodeSource.Create(projects, desktop);

        var first = sample.Collector.Scan(source);
        var capture = first.Result!.Capture;
        var entries = sample.Store.Manifest(capture);
        await Assert.That(first.Failure).IsNull();
        await Assert.That(capture.Source).IsEqualTo("claude-code");
        await Assert.That(capture.Record.Origin).IsEqualTo($"projects: {projects}\ndesktop-sessions: {desktop}");
        await Assert.That(entries.Select(entry => entry.Path).SequenceEqual([
            "desktop-sessions/shared.json",
            "projects/project/session.jsonl",
            "projects/project/session/subagents/agent.jsonl",
            "projects/project/session/tool-results/image.png",
            "projects/shared.json",
        ])).IsTrue();
        foreach (var entry in entries)
        {
            var prefix = entry.Path.StartsWith("projects/", StringComparison.Ordinal) ? "projects" : "desktop-sessions";
            var directory = prefix == "projects" ? projects : desktop;
            await Assert.That(entry.Origin).IsEqualTo(Path.Combine(directory, entry.Path[(prefix.Length + 1)..]));
            using var saved = sample.Store.Open(capture, entry.Path);
            using var output = new MemoryStream();
            saved.CopyTo(output);
            await Assert.That(output.ToArray().SequenceEqual(File.ReadAllBytes(entry.Origin))).IsTrue();
        }
        var second = sample.Collector.Scan(source).Result!;
        await Assert.That(second.NewBlocks).IsEqualTo(0);
        await Assert.That(second.Capture.Record.Manifest!.Hash).IsEqualTo(capture.Record.Manifest!.Hash);
    }

    [Test]
    public async Task MissingDesktopDirectoryKeepsProjectFilesAndReportsGap()
    {
        using var sample = new CaptureSample();
        sample.File("logs/project/session.jsonl", "available\n");
        var missing = Path.Combine(sample.SourceDirectory, "missing-indexes");
        var source = ClaudeCodeSource.Create(Path.Combine(sample.SourceDirectory, "logs"), missing);
        var round = sample.Collector.Scan(source);
        var capture = round.Result!.Capture;

        await Assert.That(round.Failure!.Contains(missing, StringComparison.Ordinal)).IsTrue();
        await Assert.That(sample.Store.Manifest(capture).Single().Path).IsEqualTo("projects/project/session.jsonl");
        await Assert.That(sample.Store.Line(new Position("claude-code", capture.Name, "projects/project/session.jsonl", 1))).IsEqualTo("available");
    }
}
