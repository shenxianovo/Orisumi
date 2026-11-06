using System.Security.Cryptography;
using System.Text;
using ZstdSharp;

namespace Orisumi.Raw.Tests;

public class BlockStoreTests
{
    [Test]
    public async Task NameIsTheAlgorithmAndTheHashOfTheContent()
    {
        var content = Encoding.UTF8.GetBytes("原件");
        var name = BlockName.Of(content);
        await Assert.That(name.ToString()).IsEqualTo($"sha256-{Convert.ToHexStringLower(SHA256.HashData(content))}");
        await Assert.That(BlockName.Parse(name.ToString())).IsEqualTo(name);
        await Assert.That(name.Abbreviation).IsEqualTo(name.Hex[..12]);
        await Assert.That(BlockName.TryParse("sha1-" + name.Hex, out _)).IsFalse();
        await Assert.That(BlockName.TryParse("sha256-" + name.Hex.ToUpperInvariant(), out _)).IsFalse();
    }

    [Test]
    public async Task SameContentIsStoredOnce()
    {
        var blocks = new DirectoryBlockStore(Samples.Directory());
        var content = Encoding.UTF8.GetBytes(Samples.Lines(1000));
        var first = blocks.Put(content);
        var second = blocks.Put(content);
        await Assert.That(first.Added).IsTrue();
        await Assert.That(second.Added).IsFalse();
        await Assert.That(blocks.Names().ToList()).IsEquivalentTo(new[] { first.Name });
    }

    [Test]
    public async Task ConcurrentWritersPublishTheSameBlockOnce()
    {
        var content = Samples.Noise(1024 * 1024);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var directory = Samples.Directory();
            using var start = new Barrier(3);
            var writes = Enumerable.Range(0, 3).Select(_ => Task.Factory.StartNew(() =>
            {
                var blocks = new DirectoryBlockStore(directory);
                if (!start.SignalAndWait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Concurrent writers did not start");
                return blocks.Put(content);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));

            var results = await Task.WhenAll(writes);
            var reader = new DirectoryBlockStore(directory);
            await Assert.That(results.Count(result => result.Added)).IsEqualTo(1);
            await Assert.That(reader.Names().ToList()).IsEquivalentTo(new[] { results[0].Name });
            await Assert.That(reader.Read(results[0].Name).SequenceEqual(content)).IsTrue();
        }
    }

    [Test]
    public async Task BlocksSitUnderTheFirstTwoCharactersOfTheirName()
    {
        var root = Samples.Directory();
        var blocks = new DirectoryBlockStore(root);
        var (name, _) = blocks.Put(Samples.Noise(1000));
        await Assert.That(blocks.PathOf(name)).IsEqualTo(Path.Combine(root, name.Hex[..2], name.ToString()));
    }

    [Test]
    public async Task CompressedBlockDecompressesWithPlainZstdToItsName()
    {
        var blocks = new DirectoryBlockStore(Samples.Directory());
        var (name, _) = blocks.Put(Encoding.UTF8.GetBytes(Samples.Lines(1000)));
        using var decompressor = new Decompressor();
        var content = decompressor.Unwrap(File.ReadAllBytes(blocks.PathOf(name))).ToArray();
        await Assert.That(BlockName.Of(content)).IsEqualTo(name);
    }

    [Test]
    public async Task IncompressibleContentIsStoredAsIs()
    {
        var blocks = new DirectoryBlockStore(Samples.Directory());
        var content = Samples.Noise(100_000);
        var (name, _) = blocks.Put(content);
        await Assert.That(File.ReadAllBytes(blocks.PathOf(name)).SequenceEqual(content)).IsTrue();
    }

    [Test]
    public async Task ContentThatStartsLikeZstdReadsBackUnchanged()
    {
        var blocks = new DirectoryBlockStore(Samples.Directory());
        byte[] content = [0x28, 0xB5, 0x2F, 0xFD, .. Samples.Noise(100_000)];
        var (name, _) = blocks.Put(content);
        await Assert.That(blocks.Read(name).SequenceEqual(content)).IsTrue();
    }

    [Test]
    public async Task DamagedBlockIsRejected()
    {
        var blocks = new DirectoryBlockStore(Samples.Directory());
        var content = Samples.Noise(100_000);
        var (name, _) = blocks.Put(content);
        content[500] ^= 1;
        File.WriteAllBytes(blocks.PathOf(name), content);
        await Assert.That(() => blocks.Read(name)).Throws<InvalidDataException>();
    }
}
