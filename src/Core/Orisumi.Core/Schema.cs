using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orisumi.Core;

public sealed record Schema(string DisplayName, string Description, string ResourceName, JsonElement Fields,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? StateFields = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? ClaimFields = null);
public sealed record SchemaRevision(Guid SchemaRevisionId, Guid SchemaId, DateTimeOffset RecordedAt, Schema Content);
public sealed record ThingType(Guid SchemaId, string DisplayName, string Description, string ResourceName);
