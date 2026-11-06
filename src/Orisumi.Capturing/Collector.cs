using System.Runtime.CompilerServices;
using Orisumi.Raw;

namespace Orisumi.Capturing;

public sealed record CaptureRound(CaptureResult? Result, string? StorageFailure = null)
{
    public string? Failure => StorageFailure ?? Result?.Capture.Record.Failure?.Reason;
}

public sealed class Collector
{
    private readonly string _storeDirectory;
    private readonly string _collector;
    private readonly StableFileReader _reader;
    private readonly SqliteSnapshotReader _databases;

    public Collector(string storeDirectory, string collector, int retries = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retries);
        _storeDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(storeDirectory));
        _collector = collector;
        _reader = new StableFileReader(retries);
        _databases = new SqliteSnapshotReader(retries);
    }

    public CaptureRound Scan(CaptureSource source, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Validate(source);
        CaptureWriter? writer = null;
        try
        {
            var store = new RawStore(_storeDirectory);
            writer = store.BeginCapture(new CaptureStart(source.Name, _collector, source.Origin), token);
            var failures = new List<string>();
            var saved = 0;
            foreach (var root in source.Roots)
            {
                foreach (var file in Files(root, failures, token))
                {
                    token.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(root.Directory, file).Replace(Path.DirectorySeparatorChar, '/');
                    if (root.Include is not null && !root.Include(relative)) continue;
                    var path = string.IsNullOrEmpty(root.Prefix) ? relative : $"{root.Prefix}/{relative}";
                    if (Save(writer, new FileInput(file, path), failures, token)) saved++;
                }
            }
            foreach (var file in source.Files)
            {
                token.ThrowIfCancellationRequested();
                if (!InsideStore(Path.GetFullPath(file.SourcePath)) && Save(writer, file, failures, token)) saved++;
            }
            token.ThrowIfCancellationRequested();
            var failure = failures.Count == 0 ? null : new CaptureFailure(string.Join('\n', failures.Order(StringComparer.Ordinal)));
            var result = failure is not null && saved == 0 ? writer.Fail(failure.Reason) : writer.Complete(failure);
            return new CaptureRound(result);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            token.ThrowIfCancellationRequested();
            var reason = $"原件存储失败：{error.Message}";
            try
            {
                if (writer is not null) return new CaptureRound(writer.Fail(reason));
            }
            catch (Exception recordingError) when (recordingError is IOException or UnauthorizedAccessException)
            {
                reason += $"；失败记录也无法保存：{recordingError.Message}";
            }
            return new CaptureRound(null, reason);
        }
    }

    public async IAsyncEnumerable<CaptureRound> Watch(CaptureSource source, TimeSpan interval,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        if (interval <= TimeSpan.Zero || interval.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(interval), "扫描间隔必须大于零且不超过约 49 天");
        while (true)
        {
            token.ThrowIfCancellationRequested();
            yield return Scan(source, token);
            await Task.Delay(interval, token);
        }
    }

    private bool Save(CaptureWriter writer, FileInput input, List<string> failures, CancellationToken token)
    {
        var origin = Path.GetFullPath(input.SourcePath);
        var attributes = Attributes(origin, failures, input.Optional);
        if (attributes is null) return false;
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            var target = LinkTarget(origin, attributes.Value, failures);
            if (target is null) return false;
            writer.Add(input.Path, origin, Stream.Null, "readlink", target);
            if (input.Method == FileInputMethod.SqliteBackup)
                failures.Add($"{origin}：数据库路径是符号链接，已保存链接目标，未取得数据库快照");
            return true;
        }
        using var snapshot = Read(input, failures, token);
        if (snapshot is null) return false;
        writer.Add(input.Path, origin, snapshot, input.Method == FileInputMethod.SqliteBackup ? "sqlite-online-backup" : "file-bytes");
        return true;
    }

    private FileStream? Read(FileInput input, List<string> failures, CancellationToken token)
    {
        try
        {
            return input.Method == FileInputMethod.SqliteBackup
                ? _databases.Read(input.SourcePath, token)
                : _reader.Read(input.SourcePath, token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            failures.Add(error.Message);
            return null;
        }
    }

    private IEnumerable<string> Files(FileRoot root, List<string> failures, CancellationToken token)
    {
        var pending = new Stack<string>();
        var directory = Path.GetFullPath(root.Directory);
        pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            token.ThrowIfCancellationRequested();
            if (InsideStore(current)) continue;
            var directoryAttributes = Attributes(current, failures, current == directory && root.Optional);
            if (directoryAttributes is null) continue;
            if ((directoryAttributes & FileAttributes.ReparsePoint) != 0)
            {
                if (current == directory) failures.Add($"{current}：来源根目录是符号链接，未遍历");
                else yield return current;
                continue;
            }
            foreach (var entry in Entries(current, failures))
            {
                token.ThrowIfCancellationRequested();
                var attributes = Attributes(entry, failures);
                if (attributes is null || InsideStore(entry)) continue;
                if ((attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0) pending.Push(entry);
                else yield return entry;
            }
        }
    }

    private bool InsideStore(string path) => path == _storeDirectory
        || path.StartsWith(_storeDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static string[] Entries(string path, List<string> failures)
    {
        try
        {
            return Directory.GetFileSystemEntries(path).Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            failures.Add($"{path}：{error.Message}");
            return [];
        }
    }

    private static FileAttributes? Attributes(string path, List<string> failures, bool optional = false)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception error) when (optional && error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            failures.Add($"{path}：{error.Message}");
            return null;
        }
    }

    private static string? LinkTarget(string path, FileAttributes attributes, List<string> failures)
    {
        try
        {
            FileSystemInfo entry = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
            return entry.LinkTarget ?? throw new IOException("符号链接在读取目标前被替换");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            failures.Add($"{path}：{error.Message}");
            return null;
        }
    }

    private static void Validate(CaptureSource source)
    {
        if (string.IsNullOrWhiteSpace(source.Name) || source.Name.Contains('/'))
            throw new ArgumentException("来源名不能为空或包含 /", nameof(source));
        if (source.Roots.Count == 0 && source.Files.Count == 0) throw new ArgumentException("至少需要一个来源目录或文件", nameof(source));
        foreach (var root in source.Roots)
        {
            if (root.Prefix.Split('/').Any(part => part is "." or "..") || root.Prefix.Contains('\\')
                || root.Prefix.StartsWith('/') || root.Prefix.EndsWith('/'))
                throw new ArgumentException("保存路径前缀必须是规范的相对路径", nameof(source));
        }
        foreach (var file in source.Files)
        {
            if (string.IsNullOrEmpty(file.Path) || file.Path.StartsWith('/') || file.Path.EndsWith('/')
                || file.Path.Split('/').Any(part => part is "" or "." or ".."))
                throw new ArgumentException("保存路径必须是规范的相对路径", nameof(source));
            if (!Enum.IsDefined(file.Method)) throw new ArgumentException("文件取得方式无效", nameof(source));
        }
    }
}
