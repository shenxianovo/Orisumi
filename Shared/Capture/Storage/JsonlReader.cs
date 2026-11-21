using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Orisumi.Capture.Storage;

public sealed record JsonlCursor(string Identity, string Path, long Offset, int LineNumber,
    long Length, long Modified, string Prefix, string Tail, string? CommittedHash = null);

public enum JsonlChange { Unchanged, Appended, Initial, Replaced, Truncated, Rewritten, Rechecked, ArchiveVersion }

public readonly record struct JsonlReadPlan(JsonlChange Change, long Offset, int LineNumber)
{
    public bool Resets => Change is not (JsonlChange.Unchanged or JsonlChange.Appended);
}

// File and cursor mechanics only; callers supply their previous cursor and decide which facts to write.
public sealed class JsonlReader : IDisposable
{
    private const int CheckpointBytes = 4096;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    private readonly FileStream stream;
    private readonly long length;
    private readonly long modified;
    private readonly Func<Stream, Stream>? decodeArchive;
    private readonly bool trackCommittedPrefix;
    private readonly CancellationToken stop;
    public string Path { get; }
    public string Identity { get; }
    public long Length => length;

    public JsonlReader(string path, Func<Stream, Stream>? decodeArchive = null, bool trackCommittedPrefix = false,
        CancellationToken stop = default)
    {
        Path = path;
        this.decodeArchive = decodeArchive;
        this.trackCommittedPrefix = trackCommittedPrefix;
        this.stop = stop;
        stream = Open(path);
        try
        {
            Identity = FileIdentity(stream);
            length = stream.Length;
            modified = File.GetLastWriteTimeUtc(stream.SafeFileHandle).Ticks;
        }
        catch { stream.Dispose(); throw; }
    }

    public static string Identify(string path)
    {
        using var stream = Open(path);
        return FileIdentity(stream);
    }

    public JsonlReadPlan Plan(JsonlCursor? previous, bool reconcile = false, bool wasArchive = false)
    {
        if (previous is null) return new(JsonlChange.Initial, 0, 0);
        var change = ChangeSince(previous, reconcile, wasArchive);
        return change is JsonlChange.Unchanged or JsonlChange.Appended
            ? new(change, previous.Offset, previous.LineNumber)
            : new(change, 0, 0);
    }

    private JsonlChange ChangeSince(JsonlCursor previous, bool reconcile, bool wasArchive)
    {
        var archive = decodeArchive is not null;
        var unchanged = previous.Identity == Identity && previous.Length == length && previous.Modified == modified && wasArchive == archive;
        if (unchanged && (archive || !reconcile)) return JsonlChange.Unchanged;
        if (archive || wasArchive) return JsonlChange.ArchiveVersion;
        if (previous.Identity != Identity) return JsonlChange.Replaced;
        if (length < previous.Offset) return JsonlChange.Truncated;
        if (Checkpoint(stream, previous.Offset, true) != previous.Prefix ||
            Checkpoint(stream, previous.Offset, false) != previous.Tail)
            return JsonlChange.Rewritten;
        if (trackCommittedPrefix && (reconcile || length <= previous.Length) &&
            HashCommittedBlocks(previous.Offset) != previous.CommittedHash)
            return JsonlChange.Rewritten;
        if (unchanged) return JsonlChange.Unchanged;
        return length == previous.Length ? JsonlChange.Rechecked : JsonlChange.Appended;
    }

    public (JsonlCursor Cursor, long BytesRead) Read(JsonlReadPlan plan, Action<int, string> observe,
        JsonlCursor? previous = null)
    {
        var offset = plan.Offset;
        var lineNumber = plan.LineNumber;
        if (decodeArchive is not null)
        {
            using var decoded = decodeArchive(stream);
            using var reader = new StreamReader(decoded, Utf8);
            // Archives are immutable versions, so their final line can end at EOF.
            while (reader.ReadLine() is { } line)
            {
                stop.ThrowIfCancellationRequested();
                observe(++lineNumber, line);
            }
            if (stream.Length != length || File.GetLastWriteTimeUtc(stream.SafeFileHandle).Ticks != modified)
                throw new IOException($"{Path} changed while being decoded");
            return (new(Identity, Path, length, lineNumber, length, modified, "", ""), stream.Position);
        }

        PrefixHash? committed = null;
        if (trackCommittedPrefix)
        {
            committed = new PrefixHash(plan.Resets ? null : previous?.CommittedHash);
            var partial = (int)(offset % CheckpointBytes);
            if (partial != 0)
            {
                Span<byte> prefix = stackalloc byte[partial];
                stream.Position = offset - partial;
                stream.ReadExactly(prefix);
                committed.Append(prefix);
            }
        }
        foreach (var (line, end) in Lines(stream, offset, length, committed, stop))
        {
            stop.ThrowIfCancellationRequested();
            observe(++lineNumber, line);
            offset = end;
        }
        return (new(Identity, Path, offset, lineNumber, length, modified,
            Checkpoint(stream, offset, true), Checkpoint(stream, offset, false), committed?.Value),
            stream.Position - plan.Offset);
    }

    public void Dispose() => stream.Dispose();

    private string HashCommittedBlocks(long offset)
    {
        var hash = new PrefixHash();
        stream.Position = 0;
        var buffer = new byte[64 * 1024];
        var remaining = offset - offset % CheckpointBytes;
        while (remaining > 0)
        {
            stop.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, remaining);
            stream.ReadExactly(buffer.AsSpan(0, count));
            hash.Append(buffer.AsSpan(0, count));
            remaining -= count;
        }
        return hash.Value;
    }

    // Chain complete fixed-size blocks so append can extend a persisted digest. The tail
    // checkpoint covers any remaining partial block, regardless of where a round ended.
    private sealed class PrefixHash(string? initial = null)
    {
        private const int DigestBytes = 32;
        private readonly byte[] block = initial is null ? new byte[DigestBytes + CheckpointBytes]
            : [.. Convert.FromHexString(initial), .. new byte[CheckpointBytes]];
        private int count;

        public string Value => Convert.ToHexString(block.AsSpan(0, DigestBytes));

        public void Append(ReadOnlySpan<byte> bytes)
        {
            Span<byte> digest = stackalloc byte[DigestBytes];
            while (!bytes.IsEmpty)
            {
                var take = Math.Min(CheckpointBytes - count, bytes.Length);
                bytes[..take].CopyTo(block.AsSpan(DigestBytes + count));
                count += take;
                if (count == CheckpointBytes)
                {
                    SHA256.HashData(block, digest);
                    digest.CopyTo(block);
                    count = 0;
                }
                bytes = bytes[take..];
            }
        }
    }

    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    // Device, inode and birth time survive a move and change on replacement.
    private static string FileIdentity(FileStream stream)
    {
        if (fstat(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), out var stat) != 0)
            throw new IOException($"could not identify {stream.Name}", new System.ComponentModel.Win32Exception());
        return $"{stat.Device}:{stat.Inode}:{stat.BirthSeconds}:{stat.BirthNanoseconds}";
    }

    // Hash of the first (prefix) or last (tail) bytes before offset.
    private static string Checkpoint(FileStream stream, long offset, bool prefix)
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
    private static IEnumerable<(string Text, long End)> Lines(FileStream stream, long start, long length,
        PrefixHash? committedPrefix, CancellationToken stop)
    {
        stream.Position = start;
        var buffer = new byte[64 * 1024];
        using var line = new MemoryStream();
        var position = start;
        while (position < length)
        {
            stop.ThrowIfCancellationRequested();
            var count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, length - position));
            if (count == 0) throw new IOException($"{stream.Name} was truncated while being read");
            var begin = 0;
            while (begin < count)
            {
                var end = Array.IndexOf(buffer, (byte)'\n', begin, count - begin);
                if (end < 0) { line.Write(buffer, begin, count - begin); break; }
                line.Write(buffer, begin, end - begin);
                if (committedPrefix is not null)
                {
                    committedPrefix.Append(line.GetBuffer().AsSpan(0, checked((int)line.Length)));
                    committedPrefix.Append("\n"u8);
                }
                var text = Utf8.GetString(line.GetBuffer(), 0, checked((int)line.Length)).TrimEnd('\r');
                if (position + end == line.Length && text.StartsWith('\uFEFF')) text = text[1..];
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
        [FieldOffset(0)] public int Device;
        [FieldOffset(8)] public ulong Inode;
        [FieldOffset(80)] public long BirthSeconds;
        [FieldOffset(88)] public long BirthNanoseconds;
    }
    [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
    private static extern int fstat(int descriptor, out DarwinStat stat);
}
