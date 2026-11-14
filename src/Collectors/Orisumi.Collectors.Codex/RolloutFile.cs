using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Orisumi.Collectors.Codex;

internal static class RolloutFile
{
    internal static string Identity(FileStream stream, string path)
    {
        if (!OperatingSystem.IsMacOS())
            return $"{path}:{File.GetCreationTimeUtc(path).Ticks}";
        if (fstat(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), out var stat) != 0)
            throw new IOException("could not identify the open Codex rollout", new System.ComponentModel.Win32Exception());
        return $"{stat.Device}:{stat.Inode}:{stat.BirthSeconds}:{stat.BirthNanoseconds}";
    }

    internal static string Checkpoint(FileStream stream, long offset, bool prefix)
    {
        var size = (int)Math.Min(offset, 4096);
        var bytes = new byte[size];
        var position = prefix ? 0 : offset - size;
        var read = 0;
        while (read < size)
        {
            var count = RandomAccess.Read(stream.SafeFileHandle, bytes.AsSpan(read), position + read);
            if (count == 0) return "";
            read += count;
        }
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    // StreamReader buffers past a line boundary. Track raw byte positions instead,
    // leaving an unfinished last line behind the committed cursor.
    internal static IEnumerable<(string Text, long End)> Lines(FileStream stream, long start, long length)
    {
        stream.Position = start;
        var buffer = new byte[64 * 1024];
        using var line = new MemoryStream();
        var position = start;
        while (position < length)
        {
            var count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, length - position));
            if (count == 0) break;
            var begin = 0;
            while (begin < count)
            {
                var end = Array.IndexOf(buffer, (byte)'\n', begin, count - begin);
                if (end < 0) { line.Write(buffer, begin, count - begin); break; }
                line.Write(buffer, begin, end - begin);
                yield return (Encoding.UTF8.GetString(line.GetBuffer(), 0, checked((int)line.Length)).TrimEnd('\r'), position + end + 1);
                line.SetLength(0);
                begin = end + 1;
            }
            position += count;
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct DarwinStat
    {
        [FieldOffset(0)] internal int Device;
        [FieldOffset(8)] internal ulong Inode;
        [FieldOffset(80)] internal long BirthSeconds;
        [FieldOffset(88)] internal long BirthNanoseconds;
    }
    [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
    private static extern int fstat(int descriptor, out DarwinStat stat);
}
