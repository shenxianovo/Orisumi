using Orisumi.Core;

namespace Orisumi.Application.Data;

public sealed class DataService(IDataStore store, TimeProvider clock)
{
    public Task<Observation?> ReadObservationAsync(Guid id, Guid? run = null, CancellationToken cancellationToken = default) => store.ExecuteAsync(async (tx, token) =>
    {
        if (await tx.IsObservationDeletedAsync(id, token)) throw new DataDeletedException();
        var observation = await tx.ReadObservationAsync(id, token);
        await RecordAsync(tx, run, observation is null ? [] : [id], token);
        return observation;
    }, cancellationToken);
    public Task<InputSet?> ReadInputSetAsync(Guid id, CancellationToken token = default) => store.ExecuteAsync((tx, ct) => tx.ReadInputSetAsync(id, ct), token);
    public Task<Guid> CreateRunAsync(CancellationToken token = default) => store.ExecuteAsync((tx, ct) => tx.CreateRunAsync(clock.GetUtcNow(), ct), token);
    public Task<DeleteResult?> DeleteObservationAsync(Guid id, CancellationToken token = default) => store.ExecuteAsync((tx, ct) => tx.DeleteObservationAsync(id, ct), token);
    public Task<DeleteResult?> DeleteThingAsync(Guid id, CancellationToken token = default) => store.ExecuteAsync((tx, ct) => tx.DeleteThingAsync(id, ct), token);
    public Task<bool> DeleteSchemaAsync(Guid id, CancellationToken token = default) => store.ExecuteAsync((tx, ct) => tx.DeleteSchemaAsync(id, ct), token);
    public Task<SchemaRevision?> ReadSchemaRevisionAsync(Guid id, CancellationToken token = default) => store.ExecuteAsync((tx, ct) => tx.ReadSchemaRevisionAsync(id, ct), token);
    public Task<IReadOnlyList<SchemaRevision>> ListSchemaRevisionsAsync(string? resource, CancellationToken cancellationToken = default) =>
        store.ExecuteAsync(async (tx, token) => await tx.ListSchemaRevisionsAsync(await ResolveSchemaAsync(tx, resource, token), token), cancellationToken);
    public Task<IReadOnlyList<ThingType>> ListThingTypesAsync(CancellationToken token = default) => store.ExecuteAsync((tx, ct) => tx.ListThingTypesAsync(ct), token);

    public Task<IReadOnlyList<Observation>> ListObservationsAsync(ObservationQuery query, string? resource = null,
        Func<IReadOnlyList<ObservationIndex>, IReadOnlyList<ObservationIndex>>? page = null, CancellationToken cancellationToken = default) => store.ExecuteAsync(async (tx, token) =>
    {
        query = query with { SchemaId = await ResolveSchemaAsync(tx, resource, token) ?? query.SchemaId };
        var records = await tx.ReadIndexesAsync(query, null, token);
        var view = new CurrentView(records, await tx.ReadControlsAsync(token));
        Guid? excludedType = await ResolveSchemaAsync(tx, query.ExcludeSpeakerType, token);
        var excluded = excludedType is { } type ? (await tx.ListThingsAsync(type, token)).Select(thing => thing.ThingId).ToHashSet() : [];
        var candidates = records.Where(record => Matches(record, query with { Role = null }, view, excluded)).ToArray();
        if (query.Role is not null) candidates = (await BusinessReferencesAsync(tx, candidates, token)).Where(record => Matches(record, query, view, excluded)).ToArray();
        var selected = view.Select(candidates, query.Current);
        var ordered = query.Descending ? selected.OrderBy(record => record.StartAt is null && record.EndAt is null)
            .ThenByDescending(record => record.StartAt ?? record.EndAt).ThenBy(record => record.ObservationId)
            : selected.OrderBy(record => record.StartAt is null && record.EndAt is null).ThenBy(record => record.StartAt ?? record.EndAt).ThenBy(record => record.ObservationId);
        // Paging is applied before recording reads by the HTTP layer.
        return await MaterializeAsync(tx, page is null ? ordered.ToArray() : page(ordered.ToArray()), query.Current ? view : null, token);
    }, cancellationToken);

    public Task<IReadOnlyList<Thing>> ListThingsAsync(string? resource = null, CancellationToken cancellationToken = default) => store.ExecuteAsync(async (tx, token) =>
    {
        var things = await tx.ListThingsAsync(await ResolveSchemaAsync(tx, resource, token), token);
        var records = await tx.ReadIndexesAsync(new(SchemaId: BuiltInSchemas.Name), null, token);
        var view = new CurrentView(records, await tx.ReadControlsAsync(token));
        return await NameThingsAsync(tx, things, records, view, token);
    }, cancellationToken);

    public Task<ThingView?> ReadThingAsync(Guid id, Guid? run = null, CancellationToken cancellationToken = default) => store.ExecuteAsync(async (tx, token) =>
    {
        if (await tx.IsThingDeletedAsync(id, token)) throw new DataDeletedException();
        var thing = await tx.ReadThingAsync(id, token);
        if (thing is null) return null;
        var records = await tx.ReadIndexesAsync(new(ThingId: id), null, token);
        var view = new CurrentView(records, await tx.ReadControlsAsync(token));
        var related = await BusinessReferencesAsync(tx, view.Select(records.Where(record => record.References.Any(reference => reference.Role is not null
            && view.SameThings(id, record).Contains(reference.TargetId))), true), token);
        var state = new List<ObservationIndex>(); var relations = new List<ObservationIndex>();
        var activities = new List<(Guid Schema, string Role, Guid Id)>();
        foreach (var record in related)
        {
            var group = view.SameThings(id, record);
            var about = record.References.Where(reference => reference.Role == "about").Select(reference => reference.TargetId).ToHashSet();
            if (about.Overlaps(group)) (about.IsSubsetOf(group) ? state : relations).Add(record);
            foreach (var role in record.References.Where(reference => reference.Role is "part" or "mention" && group.Contains(reference.TargetId)).Select(reference => reference.Role!).Distinct())
                activities.Add((record.SchemaId, role, record.ObservationId));
        }
        var schemas = (await tx.ListSchemaRevisionsAsync(null, token)).GroupBy(schema => schema.SchemaId).ToDictionary(group => group.Key, group => group.First().Content.ResourceName);
        var named = (await NameThingsAsync(tx, [thing], records, view, token)).Single();
        var states = await MaterializeAsync(tx, state, view, token); var links = await MaterializeAsync(tx, relations, view, token);
        await RecordAsync(tx, run, states.Concat(links).Select(record => record.ObservationId).Concat(named.NameObservationId is { } name ? [name] : []), token);
        return new ThingView(named, view.SameThings(id).Where(other => other != id).Order().ToArray(), states, links,
            activities.GroupBy(item => (item.Schema, item.Role)).Select(group => new ThingActivity(schemas[group.Key.Schema], group.Key.Role, group.Select(item => item.Id).Distinct().LongCount(),
                $"/observations?thingId={id:D}&role={group.Key.Role}&resourceName={schemas[group.Key.Schema]}")).ToArray());
    }, cancellationToken);

    public Task<IReadOnlyList<Referrer>?> ListReferrersAsync(Guid id, bool thing, bool current, string? path = null, string? resource = null,
        CancellationToken cancellationToken = default) => store.ExecuteAsync<IReadOnlyList<Referrer>?>(async (tx, token) =>
    {
        if (thing ? !await tx.ThingExistsAsync(id, token) : await tx.ReadObservationAsync(id, token) is null && !await tx.IsObservationDeletedAsync(id, token)) return null;
        var schema = await ResolveSchemaAsync(tx, resource, token);
        var records = await tx.ReadIndexesAsync(new(SchemaId: schema), id, token);
        var view = new CurrentView(records, await tx.ReadControlsAsync(token));
        var selected = view.Select(records.Where(record => (schema is null || record.SchemaId == schema)
            && record.References.Any(reference => reference.TargetId == id)), current);
        var observations = await MaterializeAsync(tx, selected, current ? view : null, token);
        var revisions = (await tx.ListSchemaRevisionsAsync(null, token)).ToDictionary(revision => revision.SchemaRevisionId);
        var inputs = (await tx.ReadInputSetsAsync(observations.Where(item => item.InputSetId is not null).Select(item => item.InputSetId!.Value).Distinct().ToArray(), token)).ToDictionary(input => input.InputSetId);
        var result = new List<Referrer>();
        foreach (var observation in observations)
        {
            var input = observation.InputSetId is { } inputId ? inputs[inputId] : null;
            var positions = ObservationRules.GetReferences(observation, revisions[observation.SchemaRevisionId].Content, input)
                .Where(reference => reference.TargetId == id && (path is null || reference.Path == path)).ToArray();
            if (positions.Length > 0) result.Add(new(observation, positions));
        }
        return result;
    }, cancellationToken);

    public Task RecordReadsAsync(Guid? run, IEnumerable<Guid> ids, CancellationToken cancellationToken = default) => store.ExecuteAsync(async (tx, token) =>
    { await RecordAsync(tx, run, ids, token); return true; }, cancellationToken);

    private static async Task RecordAsync(IDataTransaction tx, Guid? run, IEnumerable<Guid> ids, CancellationToken token)
    {
        if (run is not { } id) return;
        ObservationRules.Require(await tx.ReadRunAsync(id, token) is not null, "EVIDENCE-9", "Unknown runId.");
        await tx.RecordReadsAsync(id, ids.Distinct(), token);
    }
    private static async Task<Guid?> ResolveSchemaAsync(IDataTransaction tx, string? resource, CancellationToken token) =>
        resource is null ? null : await tx.FindSchemaAsync(resource, token) ?? throw new ResourceNotFoundException();

    private static bool Matches(ObservationIndex record, ObservationQuery query, CurrentView view, HashSet<Guid> excluded) =>
        (query.SchemaId is null || record.SchemaId == query.SchemaId) && (query.ObserverId is null || record.ObserverId == query.ObserverId)
        && (query.Formation is null || record.Formation == query.Formation) && (query.SpeakerId is null || (record.SpeakerId ?? record.ObserverId) == query.SpeakerId)
        && !excluded.Contains(record.SpeakerId ?? record.ObserverId) && (query.Search is null || record.SearchText?.Contains(query.Search, StringComparison.Ordinal) == true)
        && (query.From is null || (record.StartAt ?? record.EndAt) is { } start && CurrentView.Intersects(start, record.EndAt ?? start, query.From.Value, query.To!.Value))
        && (query.ThingId is not { } thing || record.References.Any(reference => reference.Role is not null && (query.Role is null || reference.Role == query.Role)
            && view.SameThings(thing, record).Contains(reference.TargetId)));

    private static async Task<IReadOnlyList<Thing>> NameThingsAsync(IDataTransaction tx, IReadOnlyList<Thing> things,
        IReadOnlyList<ObservationIndex> records, CurrentView view, CancellationToken token)
    {
        var names = view.Select(records.Where(record => record.SchemaId == BuiltInSchemas.Name), true);
        var observations = await tx.ReadObservationsAsync(names.Select(record => record.ObservationId).ToArray(), token);
        var indexes = names.ToDictionary(record => record.ObservationId);
        var result = new List<Thing>();
        foreach (var thing in things)
        {
            var name = observations.Where(observation => view.SameThings(thing.ThingId, indexes[observation.ObservationId]).Contains(observation.Content.GetProperty("thingId").GetGuid()))
                .OrderByDescending(observation => observation.Formation == "stated").ThenByDescending(observation => observation.RecordedAt).ThenByDescending(observation => observation.ObservationId).FirstOrDefault();
            result.Add(thing with { Name = name?.Content.GetProperty("name").GetString(), NameObservationId = name?.ObservationId });
        }
        return result;
    }
    private static async Task<IReadOnlyList<ObservationIndex>> BusinessReferencesAsync(IDataTransaction tx, IReadOnlyList<ObservationIndex> records, CancellationToken token)
    {
        var observations = (await tx.ReadObservationsAsync(records.Select(record => record.ObservationId).ToArray(), token)).ToDictionary(record => record.ObservationId);
        var schemas = (await tx.ListSchemaRevisionsAsync(null, token)).ToDictionary(revision => revision.SchemaRevisionId);
        return records.Select(record => record with { References = ObservationRules.GetReferences(observations[record.ObservationId], schemas[record.SchemaRevisionId].Content) }).ToArray();
    }

    private static async Task<IReadOnlyList<Observation>> MaterializeAsync(IDataTransaction tx, IReadOnlyList<ObservationIndex> records, CurrentView? view, CancellationToken token)
    {
        var observations = (await tx.ReadObservationsAsync(records.Select(record => record.ObservationId).ToArray(), token)).ToDictionary(record => record.ObservationId);
        var inputs = view is null ? new Dictionary<Guid, InputSet>() : (await tx.ReadInputSetsAsync(observations.Values.Where(item => item.InputSetId is not null).Select(item => item.InputSetId!.Value).Distinct().ToArray(), token)).ToDictionary(input => input.InputSetId);
        var result = new List<Observation>();
        foreach (var record in records)
        {
            var observation = observations[record.ObservationId];
            var input = view is not null && observation.InputSetId is { } id ? inputs[id] : null;
            result.Add(view is null ? observation : view.Decorate(observation, input));
        }
        return result;
    }
}
