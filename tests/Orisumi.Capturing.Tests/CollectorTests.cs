using System.Text;
using Orisumi.Raw;

namespace Orisumi.Capturing.Tests;

public class CollectorTests
{
    [Test]
    public async Task OpenWritableFileIsCapturedWithoutWaitingForItToClose()
    {
        using var sample = new CaptureSample();
        var path = sample.File("open.jsonl", "一行\n半行");
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        var round = sample.Collector.Scan(sample.Source);
        var capture = round.Result!.Capture;
        using var original = sample.Store.Open(capture, "open.jsonl");
        using var reader = new StreamReader(original);
        await Assert.That(reader.ReadToEnd()).IsEqualTo("一行\n半行");
        await Assert.That(round.Failure).IsNull();
    }

    [Test]
    public async Task MissingFileDoesNotPreventOtherFilesFromBeingSaved()
    {
        using var sample = new CaptureSample();
        sample.File("good.txt", "keep");
        var vanished = sample.File("vanished.txt", "gone");
        var source = sample.Source with
        {
            Roots = [new FileRoot(sample.SourceDirectory, Include: path =>
            {
                if (path == "vanished.txt") File.Delete(vanished);
                return true;
            })],
        };
        var round = sample.Collector.Scan(source);
        var capture = round.Result!.Capture;
        await Assert.That(capture.Record.Manifest).IsNotNull();
        await Assert.That(round.Failure!.Contains("vanished.txt", StringComparison.Ordinal)).IsTrue();
        await Assert.That(sample.Store.Manifest(capture).Select(entry => entry.Path).SequenceEqual(["good.txt"])).IsTrue();
        await Assert.That(sample.Store.Line(new Position("files", capture.Name, "good.txt", 1))).IsEqualTo("keep");
    }

    [Test]
    public async Task EmptyDirectoryAndMissingDirectoryHaveDifferentResults()
    {
        using var sample = new CaptureSample();
        var empty = sample.Collector.Scan(sample.Source);
        var missing = sample.Collector.Scan(sample.Source with { Roots = [new FileRoot(Path.Combine(sample.SourceDirectory, "missing"))] });
        await Assert.That(empty.Result!.Capture.Record.Manifest).IsNotNull();
        await Assert.That(empty.Failure).IsNull();
        await Assert.That(missing.Result!.Capture.Record.Manifest).IsNull();
        await Assert.That(missing.Failure).IsNotNull();
    }

    [Test]
    public async Task RootsHaveIndependentSelectionAndLogicalPaths()
    {
        using var sample = new CaptureSample();
        var logs = Path.Combine(sample.SourceDirectory, "logs");
        var index = Path.Combine(sample.SourceDirectory, "index");
        sample.File("logs/a.jsonl", "log");
        sample.File("logs/ignore.txt", "ignored");
        sample.File("index/a.json", "index");
        var source = sample.Source with
        {
            Roots = [new FileRoot(logs, "projects", path => path.EndsWith(".jsonl", StringComparison.Ordinal)), new FileRoot(index, "desktop-sessions")],
        };
        var capture = sample.Collector.Scan(source).Result!.Capture;
        var entries = sample.Store.Manifest(capture);
        await Assert.That(entries.Select(entry => entry.Path).SequenceEqual(["desktop-sessions/a.json", "projects/a.jsonl"])).IsTrue();
        await Assert.That(entries[1].Origin).IsEqualTo(Path.Combine(logs, "a.jsonl"));
    }

    [Test]
    public async Task UnixNamesWithBackslashesStayDistinctFromNestedFiles()
    {
        if (OperatingSystem.IsWindows()) return;
        using var sample = new CaptureSample();
        var literal = sample.File(@"a\b", "");
        byte[] bytes = [0xFF, 0x00, 0x0D, 0x0A];
        File.WriteAllBytes(literal, bytes);
        sample.File("a/b", "nested");
        var capture = sample.Collector.Scan(sample.Source).Result!.Capture;
        await Assert.That(sample.Store.Manifest(capture).Select(entry => entry.Path).SequenceEqual(["a/b", @"a\b"])).IsTrue();
        using var original = sample.Store.Open(capture, @"a\b");
        using var output = new MemoryStream();
        original.CopyTo(output);
        await Assert.That(output.ToArray().SequenceEqual(bytes)).IsTrue();
    }

    [Test]
    public async Task RepeatedScanReusesBlocksAndSourceDeletionPreservesEarlierCapture()
    {
        using var sample = new CaptureSample();
        var path = sample.File("a.txt", "original");
        var first = sample.Collector.Scan(sample.Source).Result!;
        var second = sample.Collector.Scan(sample.Source).Result!;
        File.Delete(path);
        var third = sample.Collector.Scan(sample.Source).Result!;
        await Assert.That(second.NewBlocks).IsEqualTo(0);
        await Assert.That(second.Capture.Record.Manifest!.Hash).IsEqualTo(first.Capture.Record.Manifest!.Hash);
        await Assert.That(sample.Store.Manifest(third.Capture).Count).IsEqualTo(0);
        await Assert.That(sample.Store.Line(new Position("files", first.Capture.Name, "a.txt", 1))).IsEqualTo("original");
    }

    [Test]
    public async Task OwnStorageIsExcludedAndDirectoryLinksArePreservedWithoutTraversal()
    {
        using var sample = new CaptureSample();
        sample.File("a.txt", "source");
        Directory.CreateSymbolicLink(Path.Combine(sample.SourceDirectory, "loop"), sample.SourceDirectory);
        var storage = Path.Combine(sample.SourceDirectory, "store");
        var collector = new Collector(storage, "tests");
        collector.Scan(sample.Source);
        var result = collector.Scan(sample.Source).Result!;
        var store = new RawStore(storage);
        var entries = store.Manifest(result.Capture);
        await Assert.That(entries.Select(entry => entry.Path).SequenceEqual(["a.txt", "loop"])).IsTrue();
        await Assert.That(entries[1].LinkTarget).IsEqualTo(sample.SourceDirectory);
        await Assert.That(entries[1].Method).IsEqualTo("readlink");
        await Assert.That(result.NewBlocks).IsEqualTo(0);
    }

    [Test]
    public async Task FileLinksKeepTheirOriginalTargetsIncludingBrokenAndExternalLinks()
    {
        using var sample = new CaptureSample();
        sample.File("contents.txt", "content");
        Directory.CreateDirectory(Path.Combine(sample.SourceDirectory, "links"));
        File.CreateSymbolicLink(Path.Combine(sample.SourceDirectory, "links", "relative"), "../contents.txt");
        File.CreateSymbolicLink(Path.Combine(sample.SourceDirectory, "links", "broken"), "../missing.txt");
        File.CreateSymbolicLink(Path.Combine(sample.SourceDirectory, "links", "external"), Path.Combine(sample.StoreDirectory, "outside"));
        var source = sample.Source with { Roots = [new FileRoot(sample.SourceDirectory, "files")] };
        var result = sample.Collector.Scan(source);
        var entries = sample.Store.Manifest(result.Result!.Capture);
        await Assert.That(result.Failure).IsNull();
        await Assert.That(entries.Select(entry => entry.Path).SequenceEqual([
            "files/contents.txt", "files/links/broken", "files/links/external", "files/links/relative",
        ])).IsTrue();
        await Assert.That(entries[0].Method).IsEqualTo("file-bytes");
        await Assert.That(entries[1].LinkTarget).IsEqualTo("../missing.txt");
        await Assert.That(entries[2].LinkTarget).IsEqualTo(Path.Combine(sample.StoreDirectory, "outside"));
        await Assert.That(entries[3].LinkTarget).IsEqualTo("../contents.txt");
        await Assert.That(entries.Skip(1).All(entry => entry.Method == "readlink" && entry.Bytes == 0)).IsTrue();
    }

    [Test]
    public async Task ALinkedRootReportsTheUnvisitedDirectory()
    {
        using var sample = new CaptureSample();
        sample.File("real/a.txt", "source");
        var linked = Path.Combine(sample.SourceDirectory, "linked");
        Directory.CreateSymbolicLink(linked, "real");
        var source = sample.Source with { Roots = [new FileRoot(linked, Optional: true)] };
        var round = sample.Collector.Scan(source);
        await Assert.That(round.Result!.Capture.Record.Manifest).IsNull();
        await Assert.That(round.Failure!.Contains("符号链接", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task ExplicitFilesDoNotTraverseTheirParentDirectory()
    {
        using var sample = new CaptureSample();
        var selected = sample.File("selected.txt", "keep");
        sample.File("excluded/private.txt", "outside scope");
        var source = new CaptureSource("files", sample.SourceDirectory, [])
        {
            Files = [new FileInput(selected, "index.txt")],
        };
        var round = sample.Collector.Scan(source);
        await Assert.That(round.Failure).IsNull();
        await Assert.That(sample.Store.Manifest(round.Result!.Capture).Single().Path).IsEqualTo("index.txt");
    }

    [Test]
    public async Task AutomaticLoopStartsImmediatelyAndCapturesLaterChanges()
    {
        using var sample = new CaptureSample();
        var path = sample.File("a.txt", "first");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var rounds = sample.Collector.Watch(sample.Source, TimeSpan.FromMilliseconds(2), stop.Token).GetAsyncEnumerator();
        await Assert.That(await rounds.MoveNextAsync()).IsTrue();
        var first = rounds.Current.Result!.Capture;
        File.WriteAllText(path, "second");
        await Assert.That(await rounds.MoveNextAsync()).IsTrue();
        var second = rounds.Current.Result!.Capture;
        await Assert.That(sample.Store.Line(new Position("files", first.Name, "a.txt", 1))).IsEqualTo("first");
        await Assert.That(sample.Store.Line(new Position("files", second.Name, "a.txt", 1))).IsEqualTo("second");
        stop.Cancel();
        await Assert.That(async () => await rounds.MoveNextAsync()).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task StorageFailureIsReportedWithoutPublishingSuccess()
    {
        using var sample = new CaptureSample();
        var blocked = sample.File("not-a-directory", "file");
        var round = new Collector(blocked, "tests").Scan(sample.Source);
        await Assert.That(round.Result).IsNull();
        await Assert.That(round.StorageFailure).IsNotNull();
    }

    [Test]
    public async Task BlockWriteFailureStillRecordsFailureWhenRecordsAreWritable()
    {
        using var sample = new CaptureSample();
        sample.File("a.txt", "source");
        Directory.CreateDirectory(sample.StoreDirectory);
        File.WriteAllText(Path.Combine(sample.StoreDirectory, "blocks"), "blocked");
        var round = sample.Collector.Scan(sample.Source);
        await Assert.That(round.Result).IsNotNull();
        await Assert.That(round.Result!.Capture.Record.Manifest).IsNull();
        await Assert.That(round.Failure!.StartsWith("原件存储失败", StringComparison.Ordinal)).IsTrue();
        await Assert.That(sample.Store.Captures().Count).IsEqualTo(1);
    }

    [Test]
    public async Task AutomaticLoopRecoversWhenTheStoreBecomesAvailable()
    {
        using var sample = new CaptureSample();
        sample.File("a.txt", "source");
        File.WriteAllText(sample.StoreDirectory, "blocked");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var rounds = sample.Collector.Watch(sample.Source, TimeSpan.FromMilliseconds(2), stop.Token).GetAsyncEnumerator();
        await rounds.MoveNextAsync();
        await Assert.That(rounds.Current.Result).IsNull();
        File.Delete(sample.StoreDirectory);
        await rounds.MoveNextAsync();
        await Assert.That(rounds.Current.Result!.Files).IsEqualTo(1);
        await Assert.That(rounds.Current.Failure).IsNull();
    }

    [Test]
    public async Task AutomaticLoopRecoversAfterTheStoreConfigIsRepaired()
    {
        using var sample = new CaptureSample();
        sample.File("a.txt", "source");
        Directory.CreateDirectory(sample.StoreDirectory);
        var config = Path.Combine(sample.StoreDirectory, "config.json");
        File.WriteAllText(config, "{");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var rounds = sample.Collector.Watch(sample.Source, TimeSpan.FromMilliseconds(2), stop.Token).GetAsyncEnumerator();
        await rounds.MoveNextAsync();
        await Assert.That(rounds.Current.Result).IsNull();
        await Assert.That(rounds.Current.StorageFailure!.Contains(config, StringComparison.Ordinal)).IsTrue();
        File.WriteAllText(config, """{"chunker":"orisumi-cdc-1"}""");
        await rounds.MoveNextAsync();
        await Assert.That(rounds.Current.Result!.Files).IsEqualTo(1);
        await Assert.That(rounds.Current.Failure).IsNull();
    }

    [Test]
    public async Task CancellingAScanDoesNotPublishAnIncompleteSuccess()
    {
        using var sample = new CaptureSample();
        sample.File("a.txt", "source");
        using var stop = new CancellationTokenSource();
        var source = sample.Source with
        {
            Roots = [new FileRoot(sample.SourceDirectory, Include: _ =>
            {
                stop.Cancel();
                return true;
            })],
        };
        await Assert.That(() => sample.Collector.Scan(source, stop.Token)).Throws<OperationCanceledException>();
        await Assert.That(sample.Store.Captures().Count).IsEqualTo(0);
    }
}

internal sealed class CaptureSample : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "orisumi-capturing-tests", Guid.NewGuid().ToString("N"));

    public CaptureSample()
    {
        Directory.CreateDirectory(SourceDirectory);
        Collector = new Collector(StoreDirectory, "tests");
    }

    public string SourceDirectory => Path.Combine(_directory, "source");
    public string StoreDirectory => Path.Combine(_directory, "store");
    public CaptureSource Source => new("files", SourceDirectory, [new FileRoot(SourceDirectory)]);
    public Collector Collector { get; }
    public RawStore Store => new(StoreDirectory);

    public string File(string path, string content)
    {
        var full = Path.Combine(SourceDirectory, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content, new UTF8Encoding(false));
        return full;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
