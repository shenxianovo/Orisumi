using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Orisumi.Raw;

public static class Chunker
{
    public const string Version = "orisumi-cdc-1";

    public const int MinSize = 256 * 1024;

    public const int AverageSize = 1024 * 1024;

    public const int MaxSize = 4 * 1024 * 1024;

    private const int AverageBits = 20;

    private const int Normalization = 2;

    private const ulong StrictMask = ~0UL << (64 - AverageBits - Normalization);

    private const ulong LooseMask = ~0UL << (64 - AverageBits + Normalization);

    private static readonly ulong[] Gear = [.. Enumerable.Range(0, 256)
        .Select(index => BinaryPrimitives.ReadUInt64BigEndian(SHA256.HashData([(byte)index])))];

    public static IEnumerable<byte[]> Split(Stream input, CancellationToken token = default)
    {
        var window = new byte[MaxSize];
        var length = 0;
        while (true)
        {
            length += Fill(input, window, length, token);
            if (length == 0) yield break;
            var cut = FindCut(window.AsSpan(0, length));
            yield return window[..cut];
            window.AsSpan(cut, length - cut).CopyTo(window);
            length -= cut;
        }
    }

    internal static int FindCut(ReadOnlySpan<byte> data)
    {
        if (data.Length <= MinSize) return data.Length;
        var end = Math.Min(data.Length, MaxSize);
        var middle = Math.Min(end, AverageSize);
        ulong fingerprint = 0;
        for (var index = MinSize; index < end; index++)
        {
            fingerprint = (fingerprint << 1) + Gear[data[index]];
            var mask = index < middle ? StrictMask : LooseMask;
            if ((fingerprint & mask) == 0) return index + 1;
        }
        return end;
    }

    private static int Fill(Stream input, byte[] window, int offset, CancellationToken token)
    {
        var total = 0;
        while (offset + total < window.Length)
        {
            token.ThrowIfCancellationRequested();
            var read = input.Read(window, offset + total, window.Length - offset - total);
            if (read == 0) break;
            total += read;
        }
        return total;
    }
}
