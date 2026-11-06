using System.Text;

namespace Orisumi.Raw.Tests;

public class CaptureRecoveryTests
{
    [Test]
    public async Task RestoreAcceptsATrailingDirectorySeparatorWithoutCreatingItsOwnDestination()
    {
        var store = new RawStore(Samples.Directory());
        var writer = store.BeginCapture(new CaptureStart("test", "tests", "/source"));
        writer.Add("file", "/source/file", new MemoryStream([1, 2]));
        var destination = Path.Combine(Samples.Directory(), "bundle");

        new CaptureRecovery(store).Restore(writer.Complete().Capture, destination + Path.DirectorySeparatorChar);

        await Assert.That(File.ReadAllBytes(Path.Combine(destination, "files", "file")).SequenceEqual(new byte[] { 1, 2 })).IsTrue();
    }

    [Test]
    public async Task UnixBackslashesRemainDistinctFromDirectorySeparatorsWhenRestored()
    {
        if (OperatingSystem.IsWindows()) return;
        var store = new RawStore(Samples.Directory());
        var writer = store.BeginCapture(new CaptureStart("test", "tests", "/source"));
        writer.Add(@"a\b", @"/source/a\b", new MemoryStream([1, 2]));
        writer.Add("a/b", "/source/a/b", new MemoryStream([3, 4]));
        var destination = Path.Combine(Samples.Directory(), "bundle");

        new CaptureRecovery(store).Restore(writer.Complete().Capture, destination);

        await Assert.That(File.ReadAllBytes(Path.Combine(destination, "files", @"a\b")).SequenceEqual(new byte[] { 1, 2 })).IsTrue();
        await Assert.That(File.ReadAllBytes(Path.Combine(destination, "files", "a", "b")).SequenceEqual(new byte[] { 3, 4 })).IsTrue();
    }

    [Test]
    public async Task RestoresBytesLinksAndProvenanceWithoutTheSource()
    {
        var store = new RawStore(Samples.Directory());
        var bytes = Samples.Noise(150_003);
        var writer = store.BeginCapture(new CaptureStart("test", "collector", "/source-no-longer-present"));
        writer.Add("附件/picture.bin", "/source/picture.bin", new MemoryStream(bytes), "file-bytes");
        writer.Add("empty", "/source/empty", Stream.Null, "file-bytes");
        writer.Add("shortcut", "/source/shortcut", Stream.Null, "readlink", "附件/picture.bin");
        var capture = writer.Complete().Capture;
        var destination = Path.Combine(Samples.Directory(), "recovered");

        var result = new CaptureRecovery(store).Restore(capture, destination);

        await Assert.That(result.Files).IsEqualTo(2);
        await Assert.That(result.Links).IsEqualTo(1);
        await Assert.That(File.ReadAllBytes(Path.Combine(destination, "files", "附件", "picture.bin")).SequenceEqual(bytes)).IsTrue();
        await Assert.That(File.ReadAllBytes(Path.Combine(destination, "files", "shortcut")).SequenceEqual(bytes)).IsTrue();
        await Assert.That(new FileInfo(Path.Combine(destination, "files", "shortcut")).LinkTarget).IsEqualTo("附件/picture.bin");
        await Assert.That(File.ReadAllBytes(Path.Combine(destination, "capture.json")).SequenceEqual(store.Records.Read(capture.Name))).IsTrue();
        var manifest = File.ReadAllBytes(Path.Combine(destination, "manifest.jsonl"));
        await Assert.That(BlockName.Of(manifest)).IsEqualTo(capture.Record.Manifest!.Hash);
        await Assert.That(Encoding.UTF8.GetString(manifest).Contains("file-bytes", StringComparison.Ordinal)).IsTrue();
        await Assert.That(capture.Record.CompletedAt >= capture.Record.CapturedAt).IsTrue();
        await Assert.That(() => store.Open(capture, "shortcut")).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ExportDoesNotOverwriteAnExistingFile()
    {
        var store = new RawStore(Samples.Directory());
        var writer = store.BeginCapture(new CaptureStart("test", "tests", "/source"));
        writer.Add("binary", "/source/binary", new MemoryStream([0, 255, 13, 10]));
        var capture = writer.Complete().Capture;
        var output = Path.Combine(Samples.Directory(), "file.bin");
        var recovery = new CaptureRecovery(store);
        await Assert.That(recovery.Export(capture, "binary", output)).IsEqualTo(4);
        await Assert.That(File.ReadAllBytes(output).SequenceEqual(new byte[] { 0, 255, 13, 10 })).IsTrue();
        await Assert.That(() => recovery.Export(capture, "binary", output)).Throws<IOException>();
        await Assert.That(File.ReadAllBytes(output).Length).IsEqualTo(4);
    }

    [Test]
    public async Task RestoreRejectsAnExistingDirectoryWithoutChangingIt()
    {
        var store = new RawStore(Samples.Directory());
        var capture = store.BeginCapture(new CaptureStart("test", "tests", "/source")).Complete().Capture;
        var destination = Samples.Directory();
        File.WriteAllText(Path.Combine(destination, "existing"), "keep");
        await Assert.That(() => new CaptureRecovery(store).Restore(capture, destination)).Throws<IOException>();
        await Assert.That(File.ReadAllText(Path.Combine(destination, "existing"))).IsEqualTo("keep");
    }

    [Test]
    public async Task InvalidPathsAndAncestorLinksCannotWriteOutsideTheBundle()
    {
        var invalid = new[] { "../outside", "/outside", "a/../outside" };
        if (OperatingSystem.IsWindows()) invalid = [.. invalid, "a\\outside"];
        foreach (var dangerous in invalid)
        {
            var store = new RawStore(Samples.Directory());
            var writer = store.BeginCapture(new CaptureStart("test", "tests", "/source"));
            writer.Add(dangerous, "/source", new MemoryStream([1]));
            var destination = Path.Combine(Samples.Directory(), "bundle");
            await Assert.That(() => new CaptureRecovery(store).Restore(writer.Complete().Capture, destination)).Throws<InvalidDataException>();
            await Assert.That(Directory.Exists(destination)).IsFalse();
        }
        var nestedStore = new RawStore(Samples.Directory());
        var nested = nestedStore.BeginCapture(new CaptureStart("test", "tests", "/source"));
        var outside = Samples.Directory();
        nested.Add("parent", "/source/parent", Stream.Null, "readlink", outside);
        nested.Add("parent/child", "/source/child", new MemoryStream([1]));
        await Assert.That(() => new CaptureRecovery(nestedStore).Restore(nested.Complete().Capture, Path.Combine(Samples.Directory(), "bundle")))
            .Throws<InvalidDataException>();
        await Assert.That(Directory.GetFileSystemEntries(outside).Length).IsEqualTo(0);
    }

    [Test]
    public async Task CorruptContentNeverPublishesAPartialRestoration()
    {
        var store = new RawStore(Samples.Directory());
        var writer = store.BeginCapture(new CaptureStart("test", "tests", "/source"));
        writer.Add("file", "/source/file", new MemoryStream([1, 2, 3]));
        var capture = writer.Complete().Capture;
        File.WriteAllBytes(((DirectoryBlockStore)store.Blocks).PathOf(store.Manifest(capture).Single().Blocks.Single()), [42]);
        var parent = Samples.Directory();
        var destination = Path.Combine(parent, "bundle");
        await Assert.That(() => new CaptureRecovery(store).Restore(capture, destination)).Throws<InvalidDataException>();
        await Assert.That(Directory.GetFileSystemEntries(parent).Length).IsEqualTo(0);
    }

    [Test]
    public async Task CancellationDuringContentRecoveryRemovesTemporaryFilesAndDirectories()
    {
        var original = new RawStore(Samples.Directory());
        var writer = original.BeginCapture(new CaptureStart("test", "tests", "/source"));
        writer.Add("file", "/source/file", new MemoryStream([1, 2, 3]));
        var capture = writer.Complete().Capture;
        var contentBlock = original.Manifest(capture).Single().Blocks.Single();
        foreach (var export in new[] { false, true })
        {
            using var stop = new CancellationTokenSource();
            var store = new RawStore(new CancelOnRead(original.Blocks, contentBlock, stop), original.Records, new StoreConfig(Chunker.Version));
            var parent = Samples.Directory();
            var recovery = new CaptureRecovery(store);
            if (export)
                await Assert.That(() => recovery.Export(capture, "file", Path.Combine(parent, "file"), stop.Token)).Throws<OperationCanceledException>();
            else
                await Assert.That(() => recovery.Restore(capture, Path.Combine(parent, "bundle"), stop.Token)).Throws<OperationCanceledException>();
            await Assert.That(Directory.GetFileSystemEntries(parent).Length).IsEqualTo(0);
        }
    }

    private sealed class CancelOnRead(IBlockStore store, BlockName target, CancellationTokenSource stop) : IBlockStore
    {
        public (BlockName Name, bool Added) Put(ReadOnlySpan<byte> content) => store.Put(content);

        public byte[] Read(BlockName name)
        {
            var bytes = store.Read(name);
            if (name == target) stop.Cancel();
            return bytes;
        }

        public IEnumerable<BlockName> Names() => store.Names();
    }
}
