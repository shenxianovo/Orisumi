using System.Text;

namespace Orisumi.Capturing.Tests;

public class StableFileReaderTests
{
    [Test]
    public async Task ChangedReadIsDiscardedAndRetriedFromTheBeginning()
    {
        var opens = 0;
        var reader = new StableFileReader(1, _ =>
        {
            opens++;
            var attempt = opens;
            var inspections = 0;
            return new OpenFile(new MemoryStream(Encoding.UTF8.GetBytes(attempt == 1 ? "bad" : "new")), () =>
            {
                inspections++;
                return Stamp(attempt == 1 && inspections > 1 ? 4 : 3);
            });
        });
        using var snapshot = reader.Read("test", CancellationToken.None);
        using var text = new StreamReader(snapshot);
        await Assert.That(text.ReadToEnd()).IsEqualTo("new");
        await Assert.That(opens).IsEqualTo(2);
    }

    [Test]
    public async Task ContinuouslyChangingFileStopsAfterTheConfiguredRetries()
    {
        var opens = 0;
        var reader = new StableFileReader(1, _ =>
        {
            opens++;
            var inspections = 0;
            return new OpenFile(new MemoryStream([1, 2, 3]), () => Stamp(++inspections == 1 ? 3 : 4));
        });
        await Assert.That(() => reader.Read("busy", CancellationToken.None)).Throws<IOException>();
        await Assert.That(opens).IsEqualTo(2);
    }

    [Test]
    public async Task ShortReadFailsInsteadOfSavingATruncatedFile()
    {
        var reader = new StableFileReader(0, _ => new OpenFile(new MemoryStream([1, 2]), () => Stamp(3)));
        await Assert.That(() => reader.Read("truncated", CancellationToken.None)).Throws<IOException>();
    }

    [Test]
    public async Task ReadingHasAFixedLimitInsteadOfFollowingNewBytes()
    {
        var reader = new StableFileReader(0, _ => new OpenFile(new MemoryStream([1, 2, 3, 4, 5]), () => Stamp(3)));
        using var snapshot = reader.Read("bounded", CancellationToken.None);
        await Assert.That(snapshot.Length).IsEqualTo(3);
        await Assert.That(snapshot.ReadByte()).IsEqualTo(1);
    }

    [Test]
    public async Task CancellationDiscardsTheTemporaryRead()
    {
        using var stop = new CancellationTokenSource();
        var reader = new StableFileReader(1, _ =>
        {
            stop.Cancel();
            return new OpenFile(new MemoryStream([1]), () => Stamp(1));
        });
        await Assert.That(() => reader.Read("cancelled", stop.Token)).Throws<OperationCanceledException>();
    }

    private static FileStamp Stamp(long length) => new(length, DateTime.UnixEpoch, DateTime.UnixEpoch);
}
