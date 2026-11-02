using System.Text.Json;
using Orisumi.Core;

namespace Orisumi.Application.Data;

public sealed record RecognitionKey(string? Scope, string Scheme, string Value);
public sealed record ThingReference(Guid? ThingId, Guid? SchemaId = null, RecognitionKey? Key = null);
public sealed record Recognition(Guid? ThingId, Guid? LatestObservationId);
public sealed record KeyTombstone(Guid? ThingId);
public sealed record SchemaDeclaration(Guid SchemaId, Schema Content);
public sealed record FactTime(DateTimeOffset? StartAt, DateTimeOffset? EndAt, string? TimeZone);
public sealed record ObservationWrite(Guid SchemaId, JsonElement Content, string Formation,
    RecognitionKey? Key = null, FactTime? Time = null, Guid[]? Inputs = null, string? StorageError = null,
    ThingReference? Speaker = null, Guid[]? Replaces = null, Guid? RunId = null);
public sealed record WriteRequest(ThingReference Observer, IReadOnlyList<ObservationWrite> Writes,
    IReadOnlyList<SchemaDeclaration> Schemas, long? Sequence = null, JsonElement? Description = null,
    IReadOnlyList<ThingType>? ThingTypes = null);
public sealed record WriteStreamState(long LastSequence, Dictionary<Guid, Guid> SchemaRevisionIds, Guid? DescriptionId = null);
public sealed record WriteError(int Status, string? Rule, string Detail);
public sealed record WriteResult(Observation? Observation, bool Changed, WriteError? Error = null, bool Skipped = false)
{
    public Guid[] Replaced => Changed ? Observation!.Replaces : [];
}
public sealed record WriteResponse(IReadOnlyList<WriteResult> Results, bool Duplicate = false);
public sealed record Thing(Guid ThingId, Guid SchemaId, IReadOnlyList<RecognitionKey> Keys, string? Name = null, Guid? NameObservationId = null);
public sealed record DeleteResult(IReadOnlyList<Guid> Deleted, IReadOnlyList<Guid> Dependents);
public sealed class DataDeletedException() : Exception("The requested identity has been deleted.");
public sealed class ResourceNotFoundException() : Exception("No type uses this resourceName.");
public sealed class WriteSkippedException() : Exception;
