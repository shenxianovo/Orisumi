using System.Text.Json.Serialization;

namespace Orisumi.Raw;

public sealed record CaptureRecord(
    string Source,
    string Collector,
    string Chunker,
    string Host,
    DateTimeOffset CapturedAt,
    string Origin,
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? SourceVersion,
    ManifestReference? Manifest,
    CaptureFailure? Failure)
{
    public const string KindName = "capture";

    [JsonPropertyOrder(-1)]
    public string Kind { get; init; } = KindName;

    public DateTimeOffset? CompletedAt { get; init; }
}

public sealed record ManifestReference(BlockName Hash, IReadOnlyList<BlockName> Blocks);

public sealed record CaptureFailure(string Reason);

public sealed record ManifestEntry(string Path, string Origin, long Bytes, BlockName Hash, IReadOnlyList<BlockName> Blocks,
    string? Method = null, string? LinkTarget = null);

public sealed record DeletionRecord(BlockName Target, DateTimeOffset DeletedAt, string Reason, string Host)
{
    public const string KindName = "deletion";

    [JsonPropertyOrder(-1)]
    public string Kind { get; init; } = KindName;
}

public sealed record Capture(BlockName Name, CaptureRecord Record)
{
    public string Source => Record.Source;
}
