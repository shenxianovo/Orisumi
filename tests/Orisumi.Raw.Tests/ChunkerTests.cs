namespace Orisumi.Raw.Tests;

public class ChunkerTests
{
    private const string FirstLengthsOfVersionOne = "1253777,1147334,1185714,1052695,924402,1340505,327851,1517226,935066,1115810,1907301,457434,1317448";

    [Test]
    public async Task VersionOneCutsAtItsRecordedLengths()
    {
        var lengths = Chunker.Split(new MemoryStream(Samples.Noise(16 * 1024 * 1024))).Select(piece => piece.Length).Take(13);
        await Assert.That(Chunker.Version).IsEqualTo("orisumi-cdc-1");
        await Assert.That(string.Join(',', lengths)).IsEqualTo(FirstLengthsOfVersionOne);
    }

    [Test]
    public async Task PiecesStayWithinBoundsAndRejoinToTheInput()
    {
        var input = Samples.Noise(20 * 1024 * 1024);
        var pieces = Chunker.Split(new MemoryStream(input)).ToList();
        await Assert.That(pieces.SkipLast(1).All(piece => piece.Length is >= Chunker.MinSize and <= Chunker.MaxSize)).IsTrue();
        await Assert.That(pieces.SelectMany(piece => piece).SequenceEqual(input)).IsTrue();
    }

    [Test]
    public async Task AppendingChangesOnlyTheLastPieces()
    {
        var before = Samples.Noise(12 * 1024 * 1024);
        var after = before.Concat(Samples.Noise(300 * 1024, seed: 1)).ToArray();
        await Assert.That(Samples.Pieces(after).Except(Samples.Pieces(before)).Count()).IsLessThanOrEqualTo(2);
    }

    [Test]
    public async Task InsertingInTheMiddleChangesOnlyNearbyPieces()
    {
        var before = Samples.Noise(12 * 1024 * 1024);
        var middle = 6 * 1024 * 1024;
        var after = before[..middle].Concat(Samples.Noise(1000, seed: 2)).Concat(before[middle..]).ToArray();
        await Assert.That(Samples.Pieces(after).Except(Samples.Pieces(before)).Count()).IsLessThanOrEqualTo(2);
    }

    [Test]
    public async Task EmptyInputHasNoPieces()
    {
        await Assert.That(Chunker.Split(new MemoryStream()).Count()).IsEqualTo(0);
    }
}
