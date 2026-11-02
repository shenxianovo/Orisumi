using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orisumi.Core;

public sealed record Observation(Guid ObservationId, Guid SchemaRevisionId, Guid ObserverId,
    Guid? SpeakerId, Guid? ObserverDescriptionId, string Formation, Guid? InputSetId,
    Guid[] Replaces, DateTimeOffset? StartAt, DateTimeOffset? EndAt, string? TimeZone,
    DateTimeOffset RecordedAt, JsonElement Content)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<DeniedRange>? DeniedRanges { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? BasisDenied { get; init; }
}
public sealed record DeniedRange(DateTimeOffset StartAt, DateTimeOffset EndAt);
public sealed record ObservationReference(string Path, Guid TargetId, string Target, string? Role = null);
