using ZstdSharp;

namespace Orisumi.Raw;

internal static class BlockCodec
{
    private const int CompressionLevel = 3;

    private static ReadOnlySpan<byte> ZstdMagic => [0x28, 0xB5, 0x2F, 0xFD];

    public static byte[] Encode(ReadOnlySpan<byte> content)
    {
        using var compressor = new Compressor(CompressionLevel);
        var compressed = compressor.Wrap(content);
        var keepCompressed = compressed.Length < content.Length || content.StartsWith(ZstdMagic);
        return keepCompressed ? compressed.ToArray() : content.ToArray();
    }

    public static byte[] Decode(byte[] stored)
    {
        if (!stored.AsSpan().StartsWith(ZstdMagic)) return stored;
        using var decompressor = new Decompressor();
        return decompressor.Unwrap(stored).ToArray();
    }
}
