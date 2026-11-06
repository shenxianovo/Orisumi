using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Orisumi.Raw.Tests;

internal static class Samples
{
    public static byte[] Noise(int length, int seed = 0)
    {
        var bytes = new byte[length];
        var counter = new byte[8];
        for (var offset = 0; offset < length; offset += 32)
        {
            BinaryPrimitives.WriteInt64BigEndian(counter, ((long)seed << 40) + offset);
            SHA256.HashData(counter).AsSpan(0, Math.Min(32, length - offset)).CopyTo(bytes.AsSpan(offset));
        }
        return bytes;
    }

    public static string Lines(int count, int first = 0)
    {
        var text = new StringBuilder();
        for (var line = first; line < first + count; line++)
            text.Append("{\"line\":").Append(line).Append(",\"text\":\"").Append(Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(line)))).Append("\"}\n");
        return text.ToString();
    }

    public static string Directory()
    {
        var path = Path.Combine(Path.GetTempPath(), "orisumi-raw-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(path);
        return path;
    }

    public static IEnumerable<BlockName> Pieces(byte[] input) => Chunker.Split(new MemoryStream(input)).Select(piece => BlockName.Of(piece));
}
