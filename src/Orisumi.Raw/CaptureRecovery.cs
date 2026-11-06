using System.Security.Cryptography;

namespace Orisumi.Raw;

public sealed record RecoveryResult(string Directory, int Files, int Links, long Bytes);

public sealed class CaptureRecovery(RawStore store)
{
    public RecoveryResult Restore(Capture capture, string destination, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (capture.Record.Manifest is not { } manifest)
            throw new InvalidOperationException("这份采集没有已保存的清单");
        var entries = store.Manifest(capture);
        ValidatePaths(entries);
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        EnsureNew(destination);
        var parent = Path.GetDirectoryName(destination) ?? throw new ArgumentException("恢复目录需要父目录", nameof(destination));
        Directory.CreateDirectory(parent);
        var pending = Path.Combine(parent, $".orisumi-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(pending);
        try
        {
            var files = Path.Combine(pending, "files");
            Directory.CreateDirectory(files);
            File.WriteAllBytes(Path.Combine(pending, "capture.json"), store.Records.Read(capture.Name));
            using (var content = new BlockStream(store.Blocks, manifest.Blocks))
                CopyVerified(content, Path.Combine(pending, "manifest.jsonl"), manifest.Hash, null, token);
            foreach (var entry in entries.Where(entry => entry.LinkTarget is null))
            {
                token.ThrowIfCancellationRequested();
                var path = Path.Combine(files, entry.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var content = new BlockStream(store.Blocks, entry.Blocks);
                CopyVerified(content, path, entry.Hash, entry.Bytes, token);
            }
            // Links are created last, deepest first: writes never traverse a restored ancestor link.
            foreach (var entry in entries.Where(entry => entry.LinkTarget is not null).OrderByDescending(entry => entry.Path.Count(c => c == '/')))
            {
                token.ThrowIfCancellationRequested();
                var path = Path.Combine(files, entry.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.CreateSymbolicLink(path, entry.LinkTarget!);
            }
            token.ThrowIfCancellationRequested();
            Directory.Move(pending, destination);
            return new RecoveryResult(destination, entries.Count(entry => entry.LinkTarget is null),
                entries.Count(entry => entry.LinkTarget is not null), entries.Sum(entry => entry.Bytes));
        }
        finally
        {
            if (Directory.Exists(pending)) Directory.Delete(pending, true);
        }
    }

    public long Export(Capture capture, string path, string destination, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var entry = store.Manifest(capture).SingleOrDefault(entry => entry.Path == path)
            ?? throw new FileNotFoundException($"采集记录中没有材料 {path}");
        if (entry.LinkTarget is not null) throw new InvalidOperationException("符号链接没有文件内容，请用 raw restore 恢复链接与目标文字");
        destination = Path.GetFullPath(destination);
        EnsureNew(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var pending = destination + $".{Guid.NewGuid():N}.pending";
        try
        {
            using var content = new BlockStream(store.Blocks, entry.Blocks);
            CopyVerified(content, pending, entry.Hash, entry.Bytes, token);
            token.ThrowIfCancellationRequested();
            File.Move(pending, destination);
            return entry.Bytes;
        }
        finally
        {
            File.Delete(pending);
        }
    }

    private static void EnsureNew(string path)
    {
        if (Path.Exists(path) || new FileInfo(path).LinkTarget is not null)
            throw new IOException($"目标已存在，请使用新的路径：{path}");
    }

    private static void ValidatePaths(IReadOnlyList<ManifestEntry> entries)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var parts = entry.Path.Split('/');
            if (Path.IsPathRooted(entry.Path) || OperatingSystem.IsWindows() && entry.Path.Contains('\\') || parts.Any(part => part is "" or "." or "..")
                || !names.Add(entry.Path))
                throw new InvalidDataException($"清单中的保存路径不是唯一的规范相对路径：{entry.Path}");
        }
        foreach (var name in names)
        {
            var parent = name;
            while (parent.LastIndexOf('/') is var separator && separator >= 0)
            {
                parent = parent[..separator];
                if (names.Contains(parent)) throw new InvalidDataException($"材料路径互相包含：{parent}、{name}");
            }
        }
    }

    private static void CopyVerified(Stream input, string destination, BlockName expected, long? expectedBytes, CancellationToken token)
    {
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var length = input.Read(buffer);
            if (length == 0) break;
            output.Write(buffer, 0, length);
            hash.AppendData(buffer.AsSpan(0, length));
            total += length;
        }
        if (BlockName.FromDigest(hash.GetHashAndReset()) != expected || expectedBytes is { } bytes && total != bytes)
            throw new InvalidDataException($"恢复内容的长度或哈希与保存记录不符：{destination}");
    }
}
