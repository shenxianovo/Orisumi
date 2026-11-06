namespace Orisumi.Raw.Tests;

public class StoreFileTests
{
    [Test]
    public async Task ConcurrentPublishersKeepTheWinningCompleteFile()
    {
        var directory = Samples.Directory();
        var path = Path.Combine(directory, "原件");
        var contents = Enumerable.Range(0, 3).Select(seed => Samples.Noise(1024 * 1024, seed)).ToArray();
        using var start = new Barrier(contents.Length);
        var writes = contents.Select(content => Task.Factory.StartNew(() =>
        {
            if (!start.SignalAndWait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Concurrent writers did not start");
            return StoreFile.Create(path, content);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        byte[]? observed = null;
        while (writes.Any(write => !write.IsCompleted))
        {
            if (File.Exists(path))
            {
                var bytes = File.ReadAllBytes(path);
                await Assert.That(contents.Any(content => content.SequenceEqual(bytes))).IsTrue();
                if (observed is not null) await Assert.That(bytes.SequenceEqual(observed)).IsTrue();
                observed = bytes;
            }
            await Task.Delay(1);
        }

        var results = await Task.WhenAll(writes);
        await Assert.That(results.Count(created => created)).IsEqualTo(1);
        var winner = contents[Array.IndexOf(results, true)];
        await Assert.That(File.ReadAllBytes(path).SequenceEqual(winner)).IsTrue();
        if (observed is not null) await Assert.That(observed.SequenceEqual(winner)).IsTrue();
        await Assert.That(StoreFile.Create(path, [1, 2, 3])).IsFalse();
        await Assert.That(File.ReadAllBytes(path).SequenceEqual(winner)).IsTrue();
        await Assert.That(Directory.GetFileSystemEntries(directory)).IsEquivalentTo(new[] { path });
    }

    [Test]
    public async Task ADirectoryCollisionFailsAndCleansTheUnpublishedFile()
    {
        var directory = Samples.Directory();
        var path = Path.Combine(directory, "existing");
        Directory.CreateDirectory(path);

        await Assert.That(() => StoreFile.Create(path, [1, 2, 3])).Throws<IOException>();

        await Assert.That(Directory.Exists(path)).IsTrue();
        await Assert.That(Directory.GetFileSystemEntries(directory)).IsEquivalentTo(new[] { path });
    }
}
