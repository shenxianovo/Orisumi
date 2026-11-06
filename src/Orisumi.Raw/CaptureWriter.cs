using System.Security.Cryptography;
using System.Text;

namespace Orisumi.Raw;

public sealed record CaptureResult(Capture Capture, int Files, long Bytes, int NewBlocks, long NewBytes);

public sealed class CaptureWriter
{
    private readonly RawStore _store;
    private readonly CaptureRecord _draft;
    private readonly CancellationToken _token;
    private readonly SortedDictionary<string, ManifestEntry> _entries = new(Comparer<string>.Create(ComparePaths));
    private int _newBlocks;
    private long _newBytes;

    internal CaptureWriter(RawStore store, CaptureRecord draft, CancellationToken token)
    {
        _store = store;
        _draft = draft;
        _token = token;
    }

    public void Add(string path, string origin, Stream content, string? method = null, string? linkTarget = null)
    {
        if (_entries.ContainsKey(path)) throw new InvalidOperationException($"本轮已包含保存路径 {path}");
        var (hash, blocks, bytes) = WriteBlocks(content);
        if (linkTarget is not null && bytes != 0) throw new ArgumentException("符号链接保存目标文字，不包含文件内容", nameof(content));
        _entries.Add(path, new ManifestEntry(path, origin, bytes, hash, blocks, method, linkTarget));
    }

    public CaptureResult Complete(CaptureFailure? failure = null)
    {
        using var manifest = new MemoryStream();
        foreach (var entry in _entries.Values)
        {
            _token.ThrowIfCancellationRequested();
            manifest.Write(RecordJson.Encode(entry));
            manifest.WriteByte((byte)'\n');
        }
        manifest.Position = 0;
        var (hash, blocks, _) = WriteBlocks(manifest);
        return WriteRoot(_draft with { Manifest = new ManifestReference(hash, blocks), Failure = failure });
    }

    public CaptureResult Fail(string reason) => WriteRoot(_draft with { Failure = new CaptureFailure(reason) });

    private static int ComparePaths(string left, string right) =>
        Encoding.UTF8.GetBytes(left).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(right));

    private CaptureResult WriteRoot(CaptureRecord record)
    {
        _token.ThrowIfCancellationRequested();
        record = record with { CompletedAt = RecordJson.Milliseconds(DateTimeOffset.UtcNow) };
        var (name, _) = _store.Records.Put(RecordJson.Encode(record));
        return new CaptureResult(new Capture(name, record), _entries.Count, _entries.Values.Sum(entry => entry.Bytes), _newBlocks, _newBytes);
    }

    private (BlockName Hash, IReadOnlyList<BlockName> Blocks, long Bytes) WriteBlocks(Stream content)
    {
        using var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var blocks = new List<BlockName>();
        long bytes = 0;
        foreach (var piece in Chunker.Split(content, _token))
        {
            _token.ThrowIfCancellationRequested();
            whole.AppendData(piece);
            bytes += piece.Length;
            var (name, added) = _store.Blocks.Put(piece);
            if (added)
            {
                _newBlocks++;
                _newBytes += piece.Length;
            }
            blocks.Add(name);
        }
        return (BlockName.FromDigest(whole.GetHashAndReset()), blocks, bytes);
    }
}
