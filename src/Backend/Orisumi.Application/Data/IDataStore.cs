using Orisumi.Core;

namespace Orisumi.Application.Data;

public interface IDataStore
{
    Task<T> ExecuteAsync<T>(Func<IDataTransaction, CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default);
}

public interface IDataTransaction
{
    Task<T> SavepointAsync<T>(Func<Task<T>> operation, CancellationToken token);
    Task<bool> IsHostInstanceAsync(Guid id, Guid host, CancellationToken token);
    Task<bool> ThingExistsAsync(Guid id, CancellationToken token);
    Task<bool> IsThingDeletedAsync(Guid id, CancellationToken token);
    Task<bool> IsObservationDeletedAsync(Guid id, CancellationToken token);
    Task<Thing?> ReadThingAsync(Guid id, CancellationToken token);
    Task<IReadOnlyList<Thing>> ListThingsAsync(Guid? schema, CancellationToken token);
    Task CreateThingAsync(Guid id, Guid schemaId, CancellationToken token);
    Task<Recognition?> FindRecognitionAsync(Guid schema, RecognitionKey key, CancellationToken token);
    Task<KeyTombstone?> FindTombstoneAsync(Guid schema, RecognitionKey key, CancellationToken token);
    Task RegisterRecognitionAsync(Guid schema, RecognitionKey key, Recognition target, CancellationToken token);
    Task<WriteStreamState> ReadStreamAsync(Guid observer, CancellationToken token);
    Task SaveStreamAsync(Guid observer, WriteStreamState stream, CancellationToken token);
    Task<SchemaRevision?> ReadSchemaRevisionAsync(Guid id, CancellationToken token);
    Task<IReadOnlyList<SchemaRevision>> ListSchemaRevisionsAsync(Guid? schema, CancellationToken token);
    Task<IReadOnlyList<ThingType>> ListThingTypesAsync(CancellationToken token);
    Task SaveSchemaRevisionAsync(SchemaRevision revision, CancellationToken token);
    Task SaveThingTypeAsync(ThingType type, CancellationToken token);
    Task<Guid?> FindSchemaAsync(string resource, CancellationToken token);
    Task<Observation?> ReadObservationAsync(Guid id, CancellationToken token);
    Task<IReadOnlyList<Observation>> ReadObservationsAsync(IReadOnlyList<Guid> ids, CancellationToken token);
    Task<IReadOnlyList<ObservationIndex>> ReadIndexesAsync(ObservationQuery? query, Guid? referrer, CancellationToken token);
    Task<IReadOnlyList<Observation>> ReadControlsAsync(CancellationToken token);
    Task SaveObservationAsync(Observation observation, SchemaRevision schema, InputSet? input, CancellationToken token);
    Task<InputSet?> ReadInputSetAsync(Guid id, CancellationToken token);
    Task<IReadOnlyList<InputSet>> ReadInputSetsAsync(IReadOnlyList<Guid> ids, CancellationToken token);
    Task SaveInputSetAsync(InputSet input, CancellationToken token);
    Task<Guid> CreateRunAsync(DateTimeOffset at, CancellationToken token);
    Task<Guid[]?> ReadRunAsync(Guid run, CancellationToken token);
    Task RecordReadsAsync(Guid run, IEnumerable<Guid> observations, CancellationToken token);
    Task<DeleteResult?> DeleteObservationAsync(Guid id, CancellationToken token);
    Task<DeleteResult?> DeleteThingAsync(Guid id, CancellationToken token);
    Task<bool> DeleteSchemaAsync(Guid id, CancellationToken token);
    Task<long?> ContentSizeAsync(string digest, CancellationToken token);
    Task UploadContentAsync(string digest, byte[] data, DateTimeOffset uploadedAt, CancellationToken token);
    Task<byte[]?> ReadContentAsync(string digest, CancellationToken token);
    Task PruneContentsAsync(DateTimeOffset before, CancellationToken token);
}

// Only query metadata is loaded when computing the shared view, never raw source content.
public sealed record ObservationIndex(Guid ObservationId, Guid SchemaId, Guid SchemaRevisionId, Guid ObserverId,
    Guid? SpeakerId, Guid? ObserverDescriptionId, string Formation, Guid? InputSetId, Guid[] Replaces,
    DateTimeOffset? StartAt, DateTimeOffset? EndAt, DateTimeOffset RecordedAt,
    string? ClaimKey, string? StateKey, string? SearchText, IReadOnlyList<ObservationReference> References);
public sealed record ObservationQuery(DateTimeOffset? From = null, DateTimeOffset? To = null, Guid? SchemaId = null,
    Guid? ObserverId = null, string? Formation = null, bool Current = true, bool Descending = false,
    Guid? ThingId = null, string? Role = null, Guid? SpeakerId = null, string? ExcludeSpeakerType = null, string? Search = null);
public sealed record Referrer(Observation Observation, IReadOnlyList<ObservationReference> References);
public sealed record ThingActivity(string ResourceName, string Role, long Count, string Href);
public sealed record ThingView(Thing Thing, IReadOnlyList<Guid> SameAsThingIds, IReadOnlyList<Observation> State,
    IReadOnlyList<Observation> Relations, IReadOnlyList<ThingActivity> Activity);
