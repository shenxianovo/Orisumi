using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Orisumi.Collectors.Storage;

// Incremental reading of append-only JSONL logs: a stable file identity, checkpoints
// that detect rewrites before a saved cursor, and complete lines with their byte ends.
internal static class AppendOnlyFile
{
    private const int CheckpointBytes = 4096;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    // Device, inode and birth time survive a move and change on replacement.
    internal static string Identity(FileStream stream)
    {
        if (fstat(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), out var stat) != 0)
            throw new IOException($"could not identify {stream.Name}", new System.ComponentModel.Win32Exception());
        return $"{stat.Device}:{stat.Inode}:{stat.BirthSeconds}:{stat.BirthNanoseconds}";
    }

    // Hash of the first (prefix) or last (tail) bytes before offset.
    internal static string Checkpoint(FileStream stream, long offset, bool prefix)
    {
        var bytes = new byte[(int)Math.Min(offset, CheckpointBytes)];
        var position = prefix ? 0 : offset - bytes.Length;
        var read = 0;
        while (read < bytes.Length)
        {
            var count = RandomAccess.Read(stream.SafeFileHandle, bytes.AsSpan(read), position + read);
            if (count == 0) throw new IOException($"{stream.Name} shrank while being read");
            read += count;
        }
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    // Complete lines between start and length. An unterminated last line is left for the next read.
    internal static IEnumerable<(string Text, long End)> Lines(FileStream stream, long start, long length)
    {
        stream.Position = start;
        var buffer = new byte[64 * 1024];
        using var line = new MemoryStream();
        var position = start;
        var first = start == 0;
        while (position < length)
        {
            var count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, length - position));
            if (count == 0) throw new IOException($"{stream.Name} was truncated while being read");
            var begin = 0;
            while (begin < count)
            {
                var end = Array.IndexOf(buffer, (byte)'\n', begin, count - begin);
                if (end < 0) { line.Write(buffer, begin, count - begin); break; }
                line.Write(buffer, begin, end - begin);
                var text = Utf8.GetString(line.GetBuffer(), 0, checked((int)line.Length)).TrimEnd('\r');
                if (position + end + 1 - line.Length - 1 == 0) text = text.TrimStart('﻿');
                yield return (text, position + end + 1);
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
