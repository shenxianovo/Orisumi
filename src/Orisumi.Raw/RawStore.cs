using System.Text;
using System.Text.Json;

namespace Orisumi.Raw;

public sealed record CaptureStart(string Source, string Collector, string Origin, DateTimeOffset? From = null, DateTimeOffset? To = null, string? SourceVersion = null);

public sealed class RawStore
{
    public const int MinimumPrefixLength = 8;

    private const string ConfigFile = "config.json";

    private readonly string _writingChunker;

    public RawStore(string root)
        : this(new DirectoryBlockStore(Path.Combine(root, "blocks")), new DirectoryBlockStore(Path.Combine(root, "records")), ReadConfig(root))
    {
    }

    internal RawStore(IBlockStore blocks, IBlockStore records, StoreConfig config)
    {
        Blocks = blocks;
        Records = records;
        _writingChunker = config.Chunker;
    }

    public IBlockStore Blocks { get; }

    public IBlockStore Records { get; }

    public static string Host => Environment.MachineName;

    public static string DefaultDirectory => Environment.GetEnvironmentVariable("ORISUMI_STORE")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".orisumi");

    public CaptureWriter BeginCapture(CaptureStart start, CancellationToken token = default)
    {
        if (_writingChunker != Chunker.Version)
            throw new InvalidOperationException($"存储配置使用 {_writingChunker}，当前版本使用 {Chunker.Version} 写入");
        var draft = new CaptureRecord(start.Source, start.Collector, Chunker.Version, Host, RecordJson.Milliseconds(DateTimeOffset.UtcNow),
            start.Origin, start.From, start.To, start.SourceVersion, null, null);
        return new CaptureWriter(this, draft, token);
    }

    public IReadOnlyList<Capture> Captures()
    {
        var captures = new List<Capture>();
        var deleted = new HashSet<BlockName>();
        foreach (var name in Records.Names())
        {
            var json = Records.Read(name);
            switch (RecordJson.KindOf(json))
            {
                case CaptureRecord.KindName:
                    captures.Add(new Capture(name, RecordJson.Decode<CaptureRecord>(json)));
                    break;
                case DeletionRecord.KindName:
                    deleted.Add(RecordJson.Decode<DeletionRecord>(json).Target);
                    break;
            }
        }
        return [.. captures.Where(capture => !deleted.Contains(capture.Name))
            .OrderBy(capture => capture.Record.CapturedAt).ThenBy(capture => capture.Name.Hex, StringComparer.Ordinal)];
    }

    public Capture Find(string prefix, string? source = null)
    {
        var hex = BlockName.StripAlgorithm(prefix);
        if (hex.Length < MinimumPrefixLength || !BlockName.IsLowerHex(hex))
            throw new FormatException($"采集记录前缀至少需要 {MinimumPrefixLength} 个小写十六进制字符：{prefix}");
        var matches = Captures().Where(capture => capture.Name.Hex.StartsWith(hex, StringComparison.Ordinal) && (source is null || capture.Source == source)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new KeyNotFoundException($"找不到采集记录 {prefix}"),
            _ => throw new InvalidOperationException($"{prefix} 匹配 {matches.Count} 份采集记录，请用更长的前缀"),
        };
    }

    public IReadOnlyList<ManifestEntry> Manifest(Capture capture)
    {
        if (capture.Record.Manifest is not { } manifest) return [];
        using var reader = new StreamReader(new BlockStream(Blocks, manifest.Blocks), Encoding.UTF8);
        var entries = new List<ManifestEntry>();
        while (reader.ReadLine() is { } line) entries.Add(RecordJson.Decode<ManifestEntry>(Encoding.UTF8.GetBytes(line)));
        return entries;
    }

    public Stream Open(Capture capture, string path)
    {
        var entry = Manifest(capture).FirstOrDefault(entry => entry.Path == path)
            ?? throw new FileNotFoundException($"{capture.Source}/{capture.Name.Abbreviation} 中没有文件 {path}");
        if (entry.LinkTarget is not null) throw new InvalidOperationException($"{path} 是符号链接，目标为 {entry.LinkTarget}");
        return new BlockStream(Blocks, entry.Blocks);
    }

    public IEnumerable<string> Lines(Capture capture, string path)
    {
        using var reader = new StreamReader(Open(capture, path), Encoding.UTF8);
        while (reader.ReadLine() is { } line) yield return line;
    }

    public Position ParsePosition(string text)
    {
        var (source, prefix, path, line) = Position.Split(text);
        return new Position(source, Find(prefix, source).Name, path, line);
    }

    public string Line(Position position)
    {
        var capture = Captures().FirstOrDefault(capture => capture.Name == position.Capture && capture.Source == position.Source)
            ?? throw new KeyNotFoundException($"找不到采集记录 {position.Source}/{position.Capture.Abbreviation}");
        return Lines(capture, position.Path).Skip(position.Line - 1).FirstOrDefault()
            ?? throw new ArgumentOutOfRangeException(nameof(position), $"{position.Display} 超过文件末尾");
    }

    public DeletionRecord Delete(Capture capture, string reason)
    {
        var deletion = new DeletionRecord(capture.Name, RecordJson.Milliseconds(DateTimeOffset.UtcNow), reason, Host);
        Records.Put(RecordJson.Encode(deletion));
        return deletion;
    }

    private static StoreConfig ReadConfig(string root)
    {
        var path = Path.Combine(root, ConfigFile);
        if (!File.Exists(path)) StoreFile.Create(path, [.. RecordJson.Encode(new StoreConfig(Chunker.Version)), (byte)'\n']);
        try
        {
            return JsonSerializer.Deserialize<StoreConfig>(File.ReadAllText(path), RecordJson.Options) is { Chunker.Length: > 0 } config
                ? config
                : throw new IOException($"原件存储配置缺少切块器版本：{path}");
        }
        catch (JsonException error)
        {
            throw new IOException($"无法读取原件存储配置：{path}。{error.Message}", error);
        }
    }
}

internal sealed record StoreConfig(string Chunker);
