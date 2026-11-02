using NpgsqlTypes;

namespace Orisumi.Infrastructure.Database;

internal sealed class StoredHost
{
    public Guid HostId { get; set; }
    public string Name { get; set; } = null!;
    public string Kind { get; set; } = null!;
    public DateTimeOffset? LastSeenAt { get; set; }
    public string CredentialHash { get; set; } = null!;
    public bool Revoked { get; set; }
}

internal sealed class StoredInstance
{
    public Guid ThingId { get; set; }
    public Guid HostId { get; set; }
    public string PackageId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public bool Enabled { get; set; }
    public string Settings { get; set; } = null!;
    public long Revision { get; set; }
    public long LastLocalSequence { get; set; }
    public string? Status { get; set; }
    public DateTimeOffset? ReportedAt { get; set; }
    public bool Removed { get; set; }
}


internal sealed class StoredObservation
{
    public Guid ObservationId { get; set; }
    public Guid SchemaId { get; set; }
    public Guid SchemaRevisionId { get; set; }
    public Guid ObserverId { get; set; }
    public Guid? SpeakerId { get; set; }
    public Guid? ObserverDescriptionId { get; set; }
    public string Formation { get; set; } = null!;
    public Guid? InputSetId { get; set; }
    public Guid[] Replaces { get; set; } = null!;
    public DateTimeOffset? StartAt { get; set; }
    public DateTimeOffset? EndAt { get; set; }
    public string? TimeZone { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public string Content { get; set; } = null!;
    public NpgsqlRange<DateTimeOffset>? FactRange { get; set; }
    public string? ClaimKey { get; set; }
    public string? StateKey { get; set; }
    public string? SearchText { get; set; }
}

internal sealed class StoredThing
{
    public Guid ThingId { get; set; }
    public Guid SchemaId { get; set; }
    public bool Deleted { get; set; }
}

internal sealed class DeletedObservation
{
    public Guid ObservationId { get; set; }
}

internal sealed class StoredSchemaRevision
{
    public Guid SchemaRevisionId { get; set; }
    public Guid SchemaId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public string Content { get; set; } = null!;
}

internal sealed class StoredThingType
{
    public Guid SchemaId { get; set; }
    public string ResourceName { get; set; } = null!;
    public string DisplayName { get; set; } = null!;
    public string Description { get; set; } = null!;
}

internal sealed class StoredSchema
{
    public Guid SchemaId { get; set; }
    public string ResourceName { get; set; } = null!;
}

internal sealed class StoredInputSet
{
    public Guid InputSetId { get; set; }
    public Guid[] Members { get; set; } = null!;
}

internal sealed class StoredContent
{
    public string Digest { get; set; } = null!;
    public long Size { get; set; }
    public byte[] Data { get; set; } = null!;
    public DateTimeOffset UploadedAt { get; set; }
}

internal sealed class StoredObservationContent
{
    public Guid ObservationId { get; set; }
    public string Digest { get; set; } = null!;
}

internal sealed class StoredObservationReference
{
    public Guid TargetId { get; set; }
    public Guid ObservationId { get; set; }
    public string? Role { get; set; }
}

internal sealed class StoredReplacement
{
    public Guid ReplacedId { get; set; }
    public Guid ReplacerId { get; set; }
    public bool ReplacerIsModel { get; set; }
}

internal sealed class StoredReview
{
    public Guid ObservationId { get; set; }
    public string Verdict { get; set; } = null!;
    public Guid? TargetId { get; set; }
    public string? TargetClaimKey { get; set; }
    public Guid[]? ScopeThingIds { get; set; }
    public Guid[]? ScopeSchemaIds { get; set; }
    public NpgsqlRange<DateTimeOffset>? FactRange { get; set; }
    public bool Stated { get; set; }
}

internal sealed class StoredSameAs
{
    public Guid ObservationId { get; set; }
    public Guid LeftId { get; set; }
    public Guid RightId { get; set; }
    public NpgsqlRange<DateTimeOffset>? FactRange { get; set; }
}

internal sealed class StoredAdoption
{
    public Guid ObservationId { get; set; }
    public Guid SchemaId { get; set; }
    public Guid ObserverDescriptionId { get; set; }
    public NpgsqlRange<DateTimeOffset>? FactRange { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

internal sealed class StoredRecognitionKey
{
    public Guid SchemaId { get; set; }
    public string? Scope { get; set; }
    public string Scheme { get; set; } = null!;
    public string Value { get; set; } = null!;
    public Guid? ThingId { get; set; }
    public Guid? LatestObservationId { get; set; }
}

internal sealed class StoredKeyTombstone
{
    public string KeyDigest { get; set; } = null!;
    public Guid? ThingId { get; set; }
}

internal sealed class StoredRun
{
    public Guid RunId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class StoredRunRead
{
    public Guid RunId { get; set; }
    public Guid ObservationId { get; set; }
}

internal sealed class StoredWriteStream
{
    public Guid ObserverId { get; set; }
    public long LastSequence { get; set; }
    public string SchemaRevisionIds { get; set; } = null!;
    public Guid? DescriptionId { get; set; }
}
