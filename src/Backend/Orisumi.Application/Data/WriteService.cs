using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orisumi.Core;
using Orisumi.Application.Management;
using static Orisumi.Core.ObservationRules;

namespace Orisumi.Application.Data;

public sealed class WriteService(IDataStore store, TimeProvider clock)
{
    public Task<WriteResponse> WriteAsync(WriteRequest request, Guid? host = null, CancellationToken cancellationToken = default) => WriteAsync(request, host, false, cancellationToken);
    public Task<WriteResponse> WriteAtomicAsync(WriteRequest request, CancellationToken cancellationToken = default) => WriteAsync(request, null, true, cancellationToken);

    private Task<WriteResponse> WriteAsync(WriteRequest request, Guid? host, bool atomic, CancellationToken cancellationToken) =>
        store.ExecuteAsync(async (tx, token) =>
        {
            if (host is { } hostId && (request.Observer.ThingId is not { } instance || !await tx.IsHostInstanceAsync(instance, hostId, token)))
                throw new ManagementException(403, "host_mismatch", "The Observer must be an active instance of this host.");
            var knownObserver = request.Observer.ThingId ?? (await tx.FindRecognitionAsync(request.Observer.SchemaId!.Value, request.Observer.Key!, token))?.ThingId;
            if (request.Sequence is { } incoming && knownObserver is { } known && incoming <= (await tx.ReadStreamAsync(known, token)).LastSequence)
                return new WriteResponse([], true);
            var existingTypes = (await tx.ListThingTypesAsync(token)).ToDictionary(type => type.SchemaId);
            foreach (var type in BuiltInSchemas.ThingTypes.Where(type => !existingTypes.ContainsKey(type.SchemaId)).Concat(request.ThingTypes ?? []))
            {
                ValidateType(type.DisplayName, type.Description, type.ResourceName);
                Require(!existingTypes.TryGetValue(type.SchemaId, out var existingType) || existingType.ResourceName == type.ResourceName, "SCHEMA-5", "resourceName cannot change.");
                await CheckResourceAsync(tx, type.SchemaId, type.ResourceName, token);
                Require(!(await tx.ListSchemaRevisionsAsync(type.SchemaId, token)).Any(), "SCHEMA-17", "A Schema cannot become a thing type.");
                await tx.SaveThingTypeAsync(type, token);
                existingTypes[type.SchemaId] = type;
            }
            var observer = await ResolveObserverAsync(tx, request.Observer, token);
            var stream = await tx.ReadStreamAsync(observer, token);
            if (request.Sequence is { } sequence && sequence <= stream.LastSequence) return new WriteResponse([], true);
            foreach (var declaration in BuiltInSchemas.All.Where(item => !stream.SchemaRevisionIds.ContainsKey(item.SchemaId)).Concat(request.Schemas))
            {
                ValidateSchema(declaration.Content);
                await CheckResourceAsync(tx, declaration.SchemaId, declaration.Content.ResourceName, token);
                Require(!(await tx.ListThingTypesAsync(token)).Any(type => type.SchemaId == declaration.SchemaId), "SCHEMA-17", "A thing type cannot become a Schema.");
                var revisions = await tx.ListSchemaRevisionsAsync(declaration.SchemaId, token);
                Require(revisions.All(revision => revision.Content.ResourceName == declaration.Content.ResourceName), "SCHEMA-5", "resourceName cannot change.");
                var revision = revisions.FirstOrDefault(revision => JsonElement.DeepEquals(ObservationJson.Content(revision.Content), ObservationJson.Content(declaration.Content)));
                if (revision is null)
                {
                    revision = new(Guid.CreateVersion7(), declaration.SchemaId, ObservationTime.Normalize(clock.GetUtcNow()), declaration.Content);
                    await tx.SaveSchemaRevisionAsync(revision, token);
                }
                stream.SchemaRevisionIds[declaration.SchemaId] = revision.SchemaRevisionId;
            }
            var inputs = new Dictionary<string, InputSet>();
            if (request.Description is { } description)
            {
                try
                {
                    var result = await tx.SavepointAsync(() => WriteOneAsync(tx, observer, stream with { DescriptionId = null },
                        new(BuiltInSchemas.ObserverDescription, description, "direct", new(observer.ToString("D"), "orisumi.observer-description", "self")), inputs, token), token);
                    stream = stream with { DescriptionId = result.Observation!.ObservationId };
                }
                catch (WriteSkippedException) { /* HISTORY-11: deleted descriptions are not restored by registration. */ }
            }
            var results = new List<WriteResult>();
            foreach (var write in request.Writes)
            {
                var workingInputs = new Dictionary<string, InputSet>(inputs);
                try
                {
                    var result = await tx.SavepointAsync(() => WriteOneAsync(tx, observer, stream, write, workingInputs, token), token);
                    results.Add(result); inputs = workingInputs;
                }
                catch (WriteSkippedException) { results.Add(new(null, false, Skipped: true)); }
                catch (ModelViolationException error) when (!atomic)
                {
                    results.Add(new(null, false, new(error.Conflict || error.Rule is "HISTORY-7" or "SCHEMA-5" or "HISTORY-9" ? 409 : 400, error.Rule, error.Message)));
                }
            }
            await tx.SaveStreamAsync(observer, stream with { LastSequence = request.Sequence ?? stream.LastSequence }, token);
            return new WriteResponse(results);
        }, cancellationToken);

    private static async Task CheckResourceAsync(IDataTransaction tx, Guid schema, string resource, CancellationToken token)
    {
        var owner = await tx.FindSchemaAsync(resource, token);
        Require(owner is null || owner == schema, "SCHEMA-5", "resourceName belongs to another type.");
    }

    private static async Task<Guid> ResolveObserverAsync(IDataTransaction tx, ThingReference reference, CancellationToken token)
    {
        if (reference.ThingId is { } id)
        {
            Require(IsUuid7(id), "THING-6", "thingId must be a UUIDv7.");
            Require(!await tx.IsThingDeletedAsync(id, token), "HISTORY-7", "Deleted observers cannot write.");
            if (await tx.ReadThingAsync(id, token) is null)
            {
                Require(await tx.ThingExistsAsync(id, token), "REFERENCE-3", "Observer must be a thing or reserved program instance.", true);
                await tx.CreateThingAsync(id, BuiltInSchemas.Program, token);
            }
            return id;
        }
        Require(await tx.FindTombstoneAsync(reference.SchemaId!.Value, reference.Key!, token) is null, "HISTORY-7", "Deleted observers cannot write.");
        return await RecognizeThingAsync(tx, reference.SchemaId.Value, reference.Key!, null, token);
    }

    private async Task<WriteResult> WriteOneAsync(IDataTransaction tx, Guid observer, WriteStreamState stream, ObservationWrite write,
        Dictionary<string, InputSet> inputs, CancellationToken token)
    {
        if (write.StorageError is { } error) throw new ModelViolationException("SCHEMA-2", error);
        Require(stream.SchemaRevisionIds.TryGetValue(write.SchemaId, out var revisionId), "SCHEMA-3", "This Observer has not declared the type.");
        var schema = (await tx.ReadSchemaRevisionAsync(revisionId, token))!;
        if (write.Key is { } key)
        {
            ValidateKey(key);
            if (await tx.FindTombstoneAsync(write.SchemaId, key, token) is not null) throw new WriteSkippedException();
        }
        var content = await ResolveObjectAsync(tx, schema.Content.Fields, write.Content, token);
        Guid? speaker = write.Speaker is { } spokenBy ? spokenBy.ThingId ?? await RecognizeThingAsync(tx, spokenBy.SchemaId!.Value, spokenBy.Key!, null, token) : null;
        if (speaker is { } speakerId) Require(await tx.ReadThingAsync(speakerId, token) is not null, "REFERENCE-3", "Speaker must be a saved thing.", true);
        var recognized = write.Key is { } identity ? await tx.FindRecognitionAsync(write.SchemaId, identity, token) : null;
        var previous = recognized?.LatestObservationId is { } latest ? await tx.ReadObservationAsync(latest, token) : null;
        var members = write.Inputs ?? [];
        Require(write.RunId is null || write.Inputs is null, "EVIDENCE-9", "inputs and runId are mutually exclusive.");
        if (write.RunId is { } run) members = await tx.ReadRunAsync(run, token) ?? throw new ModelViolationException("EVIDENCE-9", "Unknown runId.");
        Require(members.Distinct().Count() == members.Length, "INPUTSET-3", "Inputs must not repeat.");
        foreach (var member in members) Require(await tx.ReadObservationAsync(member, token) is not null, "REFERENCE-3", "Inputs must be saved Observations.", true);
        var time = write.Time ?? new(null, null, null);
        var replaces = (write.Replaces ?? []).Concat(previous is null ? [] : new[] { previous.ObservationId }).Distinct().ToArray();
        foreach (var id in replaces)
        {
            var target = await tx.ReadObservationAsync(id, token);
            Require(target is not null, "REFERENCE-3", "Replacement target must be saved.", true);
            Require((await tx.ReadSchemaRevisionAsync(target!.SchemaRevisionId, token))!.SchemaId == write.SchemaId,
                "OBSERVATION-13", "Replacement targets must have the same SchemaId.");
        }
        var observation = ObservationTime.Normalize(new Observation(Guid.CreateVersion7(), revisionId, observer, speaker, stream.DescriptionId,
            write.Formation, null, replaces, time.StartAt, time.EndAt, time.TimeZone, ObservationTime.Normalize(clock.GetUtcNow()), content));
        Validate(observation, schema.Content);
        await ValidateBuiltInAsync(tx, write.SchemaId, observation, token);
        var previousMembers = previous?.InputSetId is { } set ? (await tx.ReadInputSetAsync(set, token))!.Members : [];
        // OBSERVATION-19 compares the statement, fact time and actual inputs, independently of producer and revision.
        if (previous is not null && JsonElement.DeepEquals(previous.Content, content)
            && previous.StartAt == observation.StartAt && previous.EndAt == observation.EndAt && previous.TimeZone == observation.TimeZone
            && previousMembers.ToHashSet().SetEquals(members)) return new(previous, false);
        InputSet? input = null;
        if (members.Length > 0)
        {
            var inputKey = string.Join(',', members.Order());
            if (!inputs.TryGetValue(inputKey, out input))
            {
                input = new(Guid.CreateVersion7(), members);
                await tx.SaveInputSetAsync(input, token); inputs.Add(inputKey, input);
            }
            observation = observation with { InputSetId = input.InputSetId };
        }
        foreach (var binary in BinaryValue.Collect(observation, schema.Content))
        {
            var size = await tx.ContentSizeAsync(binary.Digest, token);
            Require(size is not null, "BINARY-3", "Binary content has not been uploaded.", true);
            Require(size == binary.Size, "BINARY-3", "Binary content size does not match uploaded bytes.");
        }
        await tx.SaveObservationAsync(observation, schema, input, token);
        if (write.Key is { } savedKey) await tx.RegisterRecognitionAsync(write.SchemaId, savedKey, new(null, observation.ObservationId), token);
        return new(observation, true);
    }

    private static async Task ValidateBuiltInAsync(IDataTransaction tx, Guid schemaId, Observation observation, CancellationToken token)
    {
        var content = observation.Content;
        if (schemaId == BuiltInSchemas.Review)
        {
            Require(content.GetProperty("verdict").GetString() is "confirm" or "deny", "REVIEW-1", "verdict must be confirm or deny.");
            var scope = content.GetProperty("scope");
            Require((content.GetProperty("targetId").ValueKind == JsonValueKind.Null) != (scope.ValueKind == JsonValueKind.Null), "REVIEW-2", "Exactly one of targetId and scope must have a value.");
            if (scope.ValueKind != JsonValueKind.Null)
            {
                Require(scope.GetProperty("thingIds").GetArrayLength() + scope.GetProperty("schemaIds").GetArrayLength() > 0, "REVIEW-2", "A scope must restrict things or types.");
                Require(scope.GetProperty("schemaIds").EnumerateArray().All(id => Guid.TryParse(id.GetString(), out _)), "REVIEW-2", "scope.schemaIds must be SchemaIds.");
            }
        }
        if (schemaId == BuiltInSchemas.Adoption)
        {
            Require(Guid.TryParse(content.GetProperty("schemaId").GetString(), out _), "ADOPTION-1", "schemaId must be a SchemaId.");
            var description = (await tx.ReadObservationAsync(content.GetProperty("observerDescriptionId").GetGuid(), token))!;
            Require((await tx.ReadSchemaRevisionAsync(description.SchemaRevisionId, token))!.SchemaId == BuiltInSchemas.ObserverDescription,
                "ADOPTION-1", "The adopted generation must be an Observer description.");
        }
    }

    private static void ValidateKey(RecognitionKey key) => Require(!string.IsNullOrEmpty(key.Scheme) && !string.IsNullOrEmpty(key.Value)
        && Encoding.UTF8.GetByteCount(key.Scope ?? "") + Encoding.UTF8.GetByteCount(key.Scheme) + Encoding.UTF8.GetByteCount(key.Value) <= 2048,
        "IDENTITY-7", "Recognition key must be nonempty and at most 2048 UTF-8 bytes.");

    private static async Task<Guid> RecognizeThingAsync(IDataTransaction tx, Guid schema, RecognitionKey key, string? role, CancellationToken token)
    {
        ValidateKey(key);
        if (await tx.FindTombstoneAsync(schema, key, token) is { } tombstone)
        {
            if (role is "about" or "part") throw new WriteSkippedException();
            Require(role == "mention", "HISTORY-7", "Deleted things cannot be referenced here.");
            return tombstone.ThingId!.Value;
        }
        Require((await tx.ListThingTypesAsync(token)).Any(type => type.SchemaId == schema), "SCHEMA-3", "Thing type must be declared.");
        if (await tx.FindRecognitionAsync(schema, key, token) is { ThingId: { } found }) return found;
        var id = Guid.CreateVersion7();
        await tx.CreateThingAsync(id, schema, token); await tx.RegisterRecognitionAsync(schema, key, new(id, null), token);
        return id;
    }

    private static async Task<JsonElement> ResolveObjectAsync(IDataTransaction tx, JsonElement fields, JsonElement content, CancellationToken token)
    {
        var result = JsonNode.Parse(content.GetRawText())!.AsObject();
        foreach (var field in fields.EnumerateObject())
            if (content.TryGetProperty(field.Name, out var value)) result[field.Name] = await ResolveValueAsync(tx, field.Value, value, token);
        return JsonSerializer.SerializeToElement(result);
    }

    private static async Task<JsonNode?> ResolveValueAsync(IDataTransaction tx, JsonElement definition, JsonElement value, CancellationToken token)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        switch (definition.GetProperty("type").GetString())
        {
            case "reference":
                var thing = definition.GetProperty("target").GetString() == "thing";
                Guid? target = null;
                if (value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var id))
                {
                    if (thing && await tx.IsThingDeletedAsync(id, token)) throw new ModelViolationException("REFERENCE-3", "Deleted thingId cannot be referenced.", true);
                    target = thing ? (await tx.ReadThingAsync(id, token))?.ThingId : (await tx.ReadObservationAsync(id, token))?.ObservationId;
                }
                else if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("schemaId", out var schema) && schema.TryGetGuid(out var schemaId)
                    && value.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.Object)
                {
                    var recognition = key.Deserialize<RecognitionKey>(ObservationJson.Options)!;
                    ValidateKey(recognition);
                    target = thing ? await RecognizeThingAsync(tx, schemaId, recognition, definition.GetProperty("role").GetString(), token)
                        : (await tx.FindRecognitionAsync(schemaId, recognition, token))?.LatestObservationId;
                }
                Require(target is not null || definition.GetProperty("nullable").GetBoolean(), "REFERENCE-3", "Reference does not resolve to a saved target.", true);
                return target is null ? null : JsonValue.Create(target.Value);
            case "object" when value.ValueKind == JsonValueKind.Object:
                return JsonNode.Parse((await ResolveObjectAsync(tx, definition.GetProperty("fields"), value, token)).GetRawText());
            case "array" when value.ValueKind == JsonValueKind.Array:
                var array = new JsonArray();
                foreach (var item in value.EnumerateArray()) array.Add(await ResolveValueAsync(tx, definition.GetProperty("items"), item, token));
                return array;
            default: return JsonNode.Parse(value.GetRawText());
        }
    }
}
