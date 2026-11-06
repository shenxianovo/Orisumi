using System.Text;

namespace Orisumi.Raw.Tests;

public class RawStoreTests
{
    [Test]
    public async Task CapturingTheSameFilesAgainAddsOnlyARoot()
    {
        var root = Samples.Directory();
        var store = new RawStore(root);
        var source = SourceWith(("log.jsonl", Samples.Lines(40_000)), ("index.jsonl", Samples.Lines(3)));
        var first = Capture(store, source);
        var blocksAfterFirst = store.Blocks.Names().Count();
        var second = Capture(store, source);
        await Assert.That(first.NewBlocks).IsGreaterThan(1);
        await Assert.That(second.NewBlocks).IsEqualTo(0);
        await Assert.That(store.Blocks.Names().Count()).IsEqualTo(blocksAfterFirst);
        await Assert.That(second.Capture.Record.Manifest!.Hash).IsEqualTo(first.Capture.Record.Manifest!.Hash);
        await Assert.That(store.Captures().Count).IsEqualTo(2);
    }

    [Test]
    public async Task AppendedLogAddsOnlyItsLastBlocksAndAManifest()
    {
        var store = new RawStore(Samples.Directory());
        var source = SourceWith(("log.jsonl", Samples.Lines(40_000)));
        Capture(store, source);
        File.AppendAllText(Path.Combine(source, "log.jsonl"), Samples.Lines(500, first: 40_000));
        await Assert.That(Capture(store, source).NewBlocks).IsLessThanOrEqualTo(3);
    }

    [Test]
    public async Task ManifestListsFilesByPathWithTheirBlocks()
    {
        var store = new RawStore(Samples.Directory());
        var source = SourceWith(("b.jsonl", Samples.Lines(10)), ("a/c.jsonl", Samples.Lines(20)));
        var capture = Capture(store, source).Capture;
        var manifest = store.Manifest(capture);
        await Assert.That(string.Join(',', manifest.Select(entry => entry.Path))).IsEqualTo("a/c.jsonl,b.jsonl");
        await Assert.That(manifest[1].Hash).IsEqualTo(BlockName.Of(Encoding.UTF8.GetBytes(Samples.Lines(10))));
        await Assert.That(store.Lines(capture, "b.jsonl").SequenceEqual(File.ReadLines(Path.Combine(source, "b.jsonl")))).IsTrue();
    }

    [Test]
    public async Task AFailedBlockReadDoesNotAdvanceTheStream()
    {
        var store = new RawStore(Samples.Directory());
        var capture = Capture(store, SourceWith(("log", "original"))).Capture;
        var block = store.Manifest(capture).Single().Blocks.Single();
        var path = ((DirectoryBlockStore)store.Blocks).PathOf(block);
        var stored = File.ReadAllBytes(path);
        File.Delete(path);
        using var original = store.Open(capture, "log");
        await Assert.That(() => original.ReadByte()).Throws<FileNotFoundException>();
        File.WriteAllBytes(path, stored);
        using var reader = new StreamReader(original);
        await Assert.That(reader.ReadToEnd()).IsEqualTo("original");
    }

    [Test]
    public async Task ManifestOrdersPathsByUtf8BytesIncludingSupplementaryCharacters()
    {
        var store = new RawStore(Samples.Directory());
        var source = SourceWith(("\U0001F600.jsonl", "emoji"), ("\uE000.jsonl", "private use"));
        var capture = Capture(store, source).Capture;
        await Assert.That(store.Manifest(capture).Select(entry => entry.Path).SequenceEqual(["\uE000.jsonl", "\U0001F600.jsonl"])).IsTrue();
    }

    [Test]
    public async Task PositionsAreStoredWholeShownShortAndFoundByPrefix()
    {
        var store = new RawStore(Samples.Directory());
        var capture = Capture(store, SourceWith(("log.jsonl", Samples.Lines(5)))).Capture;
        var position = new Position("test", capture.Name, "log.jsonl", 2);
        await Assert.That(position.ToString()).IsEqualTo($"test/{capture.Name}/log.jsonl:2");
        await Assert.That(position.Display).IsEqualTo($"test/{capture.Name.Hex[..12]}/log.jsonl:2");
        await Assert.That(store.ParsePosition(position.ToString())).IsEqualTo(position);
        await Assert.That(store.ParsePosition($"test/{capture.Name.Hex[..8]}/log.jsonl:2")).IsEqualTo(position);
        await Assert.That(store.Line(position)).IsEqualTo(Samples.Lines(1, first: 1).TrimEnd('\n'));
        await Assert.That(() => store.ParsePosition($"test/{capture.Name.Hex[..7]}/log.jsonl:2")).Throws<FormatException>();
    }

    [Test]
    public async Task DeletedCaptureDisappearsAndItsDeletionStays()
    {
        var store = new RawStore(Samples.Directory());
        var capture = Capture(store, SourceWith(("log.jsonl", Samples.Lines(5)))).Capture;
        store.Delete(capture, "误采");
        await Assert.That(store.Captures().Count).IsEqualTo(0);
        await Assert.That(store.Records.Names().Count()).IsEqualTo(2);
        await Assert.That(() => store.Find(capture.Name.Hex)).Throws<KeyNotFoundException>();
    }

    [Test]
    public async Task FailedCaptureKeepsARootWithItsReason()
    {
        var store = new RawStore(Samples.Directory());
        var result = store.BeginCapture(new CaptureStart("test", "tests", "/nowhere")).Fail("目录不存在");
        var stored = store.Captures().Single();
        await Assert.That(stored.Record.Failure).IsEqualTo(new CaptureFailure("目录不存在"));
        await Assert.That(stored.Record.Manifest).IsNull();
        await Assert.That(stored.Name).IsEqualTo(result.Capture.Name);
    }

    [Test]
    public async Task CancellationDuringManifestWriteDoesNotPublishARoot()
    {
        using var stop = new CancellationTokenSource();
        var blocks = new CancelAfterPut(new DirectoryBlockStore(Samples.Directory()), stop);
        var records = new DirectoryBlockStore(Samples.Directory());
        var store = new RawStore(blocks, records, new StoreConfig(Chunker.Version));
        var writer = store.BeginCapture(new CaptureStart("test", "tests", "/tmp"), stop.Token);
        writer.Add("empty", "/tmp/empty", Stream.Null);
        await Assert.That(() => writer.Complete()).Throws<OperationCanceledException>();
        await Assert.That(store.Captures().Count).IsEqualTo(0);
    }

    [Test]
    public async Task NewStoreWritesWithVersionOne()
    {
        var root = Samples.Directory();
        _ = new RawStore(root);
        await Assert.That(File.ReadAllText(Path.Combine(root, "config.json"))).IsEqualTo("{\"chunker\":\"orisumi-cdc-1\"}\n");
    }

    [Test]
    public async Task StoreWritingWithAnotherChunkerStillReads()
    {
        var root = Samples.Directory();
        var store = new RawStore(root);
        var capture = Capture(store, SourceWith(("log.jsonl", Samples.Lines(5)))).Capture;
        File.WriteAllText(Path.Combine(root, "config.json"), "{\"chunker\":\"orisumi-cdc-2\"}");
        var later = new RawStore(root);
        await Assert.That(later.Lines(later.Captures().Single(), "log.jsonl").Count()).IsEqualTo(5);
        await Assert.That(() => later.BeginCapture(new CaptureStart("test", "tests", "/tmp"))).Throws<InvalidOperationException>();
        await Assert.That(capture.Record.Chunker).IsEqualTo("orisumi-cdc-1");
    }

    private static string SourceWith(params (string Path, string Text)[] files)
    {
        var source = Samples.Directory();
        foreach (var (path, text) in files)
        {
            var full = Path.Combine(source, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        return source;
    }

    private static CaptureResult Capture(RawStore store, string source)
    {
        var writer = store.BeginCapture(new CaptureStart("test", "tests", source));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            using var content = File.OpenRead(file);
            writer.Add(Path.GetRelativePath(source, file), file, content);
        }
        return writer.Complete();
    }

    private sealed class CancelAfterPut(IBlockStore store, CancellationTokenSource stop) : IBlockStore
    {
        public (BlockName Name, bool Added) Put(ReadOnlySpan<byte> content)
        {
            var result = store.Put(content);
            stop.Cancel();
            return result;
        }

        public byte[] Read(BlockName name) => store.Read(name);

        public IEnumerable<BlockName> Names() => store.Names();
    }
}
