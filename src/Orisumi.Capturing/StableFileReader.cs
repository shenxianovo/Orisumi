namespace Orisumi.Capturing;

internal sealed record FileStamp(long Length, DateTime ModifiedAt, DateTime CreatedAt);

internal sealed record OpenFile(Stream Content, Func<FileStamp> Inspect) : IDisposable
{
    public void Dispose() => Content.Dispose();
}

internal sealed class StableFileReader(int retries, Func<string, OpenFile>? open = null)
{
    public FileStream Read(string path, CancellationToken token)
    {
        Exception? failure = null;
        for (long attempt = 0; attempt <= retries; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return ReadOnce(path, token);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                failure = error;
            }
        }
        throw new IOException($"{path}：{failure!.Message}", failure);
    }

    private FileStream ReadOnce(string path, CancellationToken token)
    {
        using var source = (open ?? Open)(path);
        var before = source.Inspect();
        var temporary = Path.Combine(Path.GetTempPath(), $"orisumi-capture-{Guid.NewGuid():N}");
        var snapshot = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536,
            FileOptions.DeleteOnClose | FileOptions.SequentialScan);
        try
        {
            Copy(source.Content, snapshot, before.Length, token);
            if (before != source.Inspect()) throw new IOException("读取期间文件发生变化");
            snapshot.Position = 0;
            return snapshot;
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    private static void Copy(Stream source, Stream target, long length, CancellationToken token)
    {
        var buffer = new byte[65536];
        while (length > 0)
        {
            token.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, length));
            if (read == 0) throw new IOException("文件在读完之前被截断");
            target.Write(buffer, 0, read);
            length -= read;
        }
        token.ThrowIfCancellationRequested();
    }

    private static OpenFile Open(string path)
    {
        var content = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            65536, FileOptions.SequentialScan);
        return new OpenFile(content, () => Inspect(content, path));
    }

    private static FileStamp Inspect(FileStream content, string path)
    {
        var handle = content.SafeFileHandle;
        var stamp = new FileStamp(content.Length, File.GetLastWriteTimeUtc(handle), File.GetCreationTimeUtc(handle));
        var file = new FileInfo(path);
        if (!file.Exists || stamp != new FileStamp(file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc))
            throw new IOException("读取期间文件被移动或替换");
        return stamp;
    }
}
