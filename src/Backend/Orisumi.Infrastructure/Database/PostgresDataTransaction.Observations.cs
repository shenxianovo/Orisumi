using System.Text.Json;
using NpgsqlTypes;
using Orisumi.Application.Data;
using Orisumi.Core;

namespace Orisumi.Infrastructure.Database;

internal sealed partial class PostgresDataTransaction
{
    public async Task SaveObservationAsync(Observation observation, SchemaRevision schema, InputSet? input, CancellationToken token)
    {
        var claim = ContentKeys.Fields(schema.SchemaId, schema.Content.ClaimFields, observation.Content, schema.SchemaId == BuiltInSchemas.SameAs);
        var state = ContentKeys.Fields(schema.SchemaId, schema.Content.StateFields, observation.Content);
        await ExecuteAsync("""
            INSERT INTO observations(observation_id,schema_id,schema_revision_id,observer_id,speaker_id,observer_description_id,formation,input_set_id,replaces,start_at,end_at,time_zone,recorded_at,content,fact_range,claim_key,state_key,search_text)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18)
            """, token, observation.ObservationId, schema.SchemaId, observation.SchemaRevisionId, observation.ObserverId,
            Typed(NpgsqlDbType.Uuid, observation.SpeakerId), Typed(NpgsqlDbType.Uuid, observation.ObserverDescriptionId), observation.Formation,
            Typed(NpgsqlDbType.Uuid, observation.InputSetId), observation.Replaces, Typed(NpgsqlDbType.TimestampTz, observation.StartAt), Typed(NpgsqlDbType.TimestampTz, observation.EndAt),
            Typed(NpgsqlDbType.Text, observation.TimeZone), observation.RecordedAt, Typed(NpgsqlDbType.Jsonb, observation.Content.GetRawText()), Range(observation),
            Typed(NpgsqlDbType.Text, claim), Typed(NpgsqlDbType.Text, state), Typed(NpgsqlDbType.Text, ObservationRules.SearchText(schema.Content, observation.Content)));
        await SaveProjectionsAsync(observation, schema, input, token);
    }
    private async Task SaveProjectionsAsync(Observation observation, SchemaRevision schema, InputSet? input, CancellationToken token)
    {
        var references = ObservationRules.GetReferences(observation, schema.Content, input);
        foreach (var group in references.GroupBy(reference => reference.TargetId))
        {
            var role = group.Select(reference => reference.Role).OrderBy(role => role switch { "about" => 0, "part" => 1, "mention" => 2, _ => 3 }).First();
            await ExecuteAsync("INSERT INTO observation_references(target_id,observation_id,role) VALUES($1,$2,$3)", token, group.Key, observation.ObservationId, Typed(NpgsqlDbType.Text, role));
        }
        await SaveContentsAsync(observation.ObservationId, BinaryValue.Collect(observation, schema.Content), token);
        await ExecuteAsync("INSERT INTO replacements(replaced_id,replacer_id,replacer_is_model) SELECT unnest($1::uuid[]),$2,$3", token,
            observation.Replaces, observation.ObservationId, ObservationRules.IsModel(observation));
        var content = observation.Content;
        if (schema.SchemaId == BuiltInSchemas.Review)
        {
            var targetId = content.GetProperty("targetId").ValueKind == JsonValueKind.Null ? (Guid?)null : content.GetProperty("targetId").GetGuid();
            var claim = targetId is { } target ? await ScalarAsync("SELECT claim_key FROM observations WHERE observation_id=$1", token, target) as string : null;
            var scope = content.GetProperty("scope");
            await ExecuteAsync("INSERT INTO reviews(observation_id,verdict,target_id,target_claim_key,scope_thing_ids,scope_schema_ids,fact_range,stated) VALUES($1,$2,$3,$4,$5,$6,$7,$8)", token,
                observation.ObservationId, content.GetProperty("verdict").GetString()!, Typed(NpgsqlDbType.Uuid, targetId), Typed(NpgsqlDbType.Text, claim),
                Typed(NpgsqlDbType.Array | NpgsqlDbType.Uuid, scope.ValueKind == JsonValueKind.Null ? null : scope.GetProperty("thingIds").EnumerateArray().Select(value => value.GetGuid()).ToArray()),
                Typed(NpgsqlDbType.Array | NpgsqlDbType.Uuid, scope.ValueKind == JsonValueKind.Null ? null : scope.GetProperty("schemaIds").EnumerateArray().Select(value => value.GetGuid()).ToArray()), Range(observation), observation.Formation == "stated");
        }
        if (schema.SchemaId == BuiltInSchemas.SameAs)
            await ExecuteAsync("INSERT INTO same_as_links(observation_id,left_id,right_id,fact_range) VALUES($1,$2,$3,$4)", token,
                observation.ObservationId, content.GetProperty("leftId").GetGuid(), content.GetProperty("rightId").GetGuid(), Range(observation));
        if (schema.SchemaId == BuiltInSchemas.Adoption)
            await ExecuteAsync("INSERT INTO adoptions(observation_id,schema_id,observer_description_id,fact_range,recorded_at) VALUES($1,$2,$3,$4,$5)", token,
                observation.ObservationId, content.GetProperty("schemaId").GetGuid(), content.GetProperty("observerDescriptionId").GetGuid(), Range(observation), observation.RecordedAt);
    }
    private static Npgsql.NpgsqlParameter Range(Observation observation)
    {
        var start = observation.StartAt ?? observation.EndAt; var end = observation.EndAt ?? start;
        return Typed(NpgsqlDbType.Range | NpgsqlDbType.TimestampTz, start is { } from ? new NpgsqlRange<DateTimeOffset>(from, true, end!.Value, from == end) : null);
    }

    public async Task<IReadOnlyList<ObservationIndex>> ReadIndexesAsync(ObservationQuery? query, Guid? referrer, CancellationToken token)
    {
        var parameters = new List<object>();
        string Parameter(object value) { parameters.Add(value); return "$" + parameters.Count.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        var conditions = new List<string>();
        if (query?.SchemaId is { } schema) conditions.Add("o.schema_id=" + Parameter(schema));
        if (query?.ObserverId is { } observer) conditions.Add("o.observer_id=" + Parameter(observer));
        if (query?.SpeakerId is { } speaker) conditions.Add("coalesce(o.speaker_id,o.observer_id)=" + Parameter(speaker));
        if (query?.Formation is { } formation) conditions.Add("o.formation=" + Parameter(formation));
        if (query?.From is { } from) conditions.Add("o.fact_range && tstzrange(" + Parameter(from) + "," + Parameter(query.To!.Value) + ",'[)')");
        if (query?.Search is { } search) conditions.Add("position(" + Parameter(search) + " in o.search_text)>0");
        if (referrer is { } target) conditions.Add("EXISTS(SELECT 1 FROM observation_references r WHERE r.observation_id=o.observation_id AND r.target_id=" + Parameter(target) + ")");
        var connected = "";
        if (query?.ThingId is { } thing)
        {
            connected = "connected(id) AS (SELECT " + Parameter(thing) + "::uuid UNION SELECT CASE WHEN l.left_id=c.id THEN l.right_id ELSE l.left_id END FROM same_as_links l JOIN connected c ON l.left_id=c.id OR l.right_id=c.id),";
            conditions.Add("EXISTS(SELECT 1 FROM observation_references r JOIN connected c ON c.id=r.target_id WHERE r.observation_id=o.observation_id AND r.role IS NOT NULL)");
        }
        // Keep control records and their targets/inputs alongside candidates so the shared view can evaluate them.
        var selection = "WITH RECURSIVE " + connected + "candidates AS (SELECT o.observation_id FROM observations o" +
            (conditions.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conditions)) + """
            ), relevant AS (
                SELECT observation_id FROM candidates
                UNION SELECT observation_id FROM reviews
                UNION SELECT observation_id FROM same_as_links
                UNION SELECT observation_id FROM adoptions
                UNION SELECT target_id FROM reviews WHERE target_id IS NOT NULL
                UNION SELECT unnest(i.members) FROM input_sets i JOIN observations o ON o.input_set_id=i.input_set_id JOIN candidates c ON c.observation_id=o.observation_id
            ), selected AS (
                SELECT observation_id FROM relevant
                UNION SELECT r.replacer_id FROM replacements r JOIN relevant v ON v.observation_id=r.replaced_id
            )
            """;
        var references = (await QueryAsync(selection + "SELECT r.observation_id,r.target_id,r.role FROM observation_references r JOIN selected s ON s.observation_id=r.observation_id", reader =>
            (Id: reader.GetGuid(0), Reference: new ObservationReference("", reader.GetGuid(1), "", reader.IsDBNull(2) ? null : reader.GetString(2))), token, parameters.ToArray())).ToLookup(item => item.Id, item => item.Reference);
        return await QueryAsync(selection + """
            SELECT o.observation_id,schema_id,schema_revision_id,observer_id,speaker_id,observer_description_id,formation,input_set_id,replaces,start_at,end_at,recorded_at,claim_key,state_key,search_text
            FROM observations o JOIN selected s ON s.observation_id=o.observation_id
            """, reader => new ObservationIndex(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3), Nullable<Guid>(reader, 4), Nullable<Guid>(reader, 5),
                reader.GetString(6), Nullable<Guid>(reader, 7), reader.GetFieldValue<Guid[]>(8), Nullable<DateTimeOffset>(reader, 9), Nullable<DateTimeOffset>(reader, 10), reader.GetFieldValue<DateTimeOffset>(11),
                reader.IsDBNull(12) ? null : reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetString(14), references[reader.GetGuid(0)].ToArray()), token, parameters.ToArray());
    }

    public async Task<DeleteResult?> DeleteObservationAsync(Guid id, CancellationToken token)
    {
        if (await IsObservationDeletedAsync(id, token)) return new([], []);
        if (await ReadObservationAsync(id, token) is null) return null;
        return await DeleteObservationsAsync([id], token);
    }
    public async Task<DeleteResult?> DeleteThingAsync(Guid id, CancellationToken token)
    {
        if (await IsThingDeletedAsync(id, token)) return new([], []);
        if (!await ThingExistsAsync(id, token)) return null;
        var observations = await QueryAsync("SELECT observation_id FROM observation_references WHERE target_id=$1 AND role IN ('about','part')", reader => reader.GetGuid(0), token, id);
        var result = await DeleteObservationsAsync(observations.ToArray(), token);
        await TombstoneKeysAsync("thing_id=$1", id, token);
        await ExecuteAsync("INSERT INTO things(thing_id,schema_id,deleted) VALUES($1,$2,true) ON CONFLICT(thing_id) DO UPDATE SET deleted=true", token, id, BuiltInSchemas.Program);
        await ExecuteAsync("DELETE FROM write_streams WHERE observer_id=$1", token, id);
        await ExecuteAsync("UPDATE observer_instances SET removed=true,revision=revision+1 WHERE thing_id=$1 AND NOT removed", token, id);
        return result with { Deleted = new[] { id }.Concat(result.Deleted).ToArray() };
    }
    private async Task<DeleteResult> DeleteObservationsAsync(Guid[] roots, CancellationToken token)
    {
        var ids = (await QueryAsync("""
            WITH RECURSIVE deleted AS (
                SELECT observation_id,replaces FROM observations WHERE observation_id=ANY($1)
                UNION SELECT o.observation_id,o.replaces FROM observations o JOIN deleted d ON o.observation_id=ANY(d.replaces)
            ) SELECT observation_id FROM deleted
            """, reader => reader.GetGuid(0), token, (object)roots)).ToArray();
        var dependents = await QueryAsync("SELECT observation_id FROM observations WHERE NOT observation_id=ANY($1) AND input_set_id IN (SELECT input_set_id FROM input_sets WHERE members && $1)", reader => reader.GetGuid(0), token, (object)ids);
        var digests = await QueryAsync("SELECT DISTINCT digest FROM observation_contents WHERE observation_id=ANY($1)", reader => reader.GetString(0), token, (object)ids);
        await TombstoneKeysAsync("latest_observation_id=ANY($1)", ids, token);
        await ExecuteAsync("INSERT INTO deleted_observations(observation_id) SELECT unnest($1::uuid[]) ON CONFLICT DO NOTHING", token, (object)ids);
        foreach (var table in new[] { "reviews", "same_as_links", "adoptions" }) await ExecuteAsync($"DELETE FROM {table} WHERE observation_id=ANY($1)", token, (object)ids);
        await ExecuteAsync("DELETE FROM replacements WHERE replacer_id=ANY($1)", token, (object)ids);
        await ExecuteAsync("UPDATE write_streams SET description_id=NULL WHERE description_id=ANY($1)", token, (object)ids);
        await ExecuteAsync("DELETE FROM observations WHERE observation_id=ANY($1)", token, (object)ids);
        await ExecuteAsync("DELETE FROM contents c WHERE digest=ANY($1) AND NOT EXISTS(SELECT 1 FROM observation_contents o WHERE o.digest=c.digest)", token, (object)digests.ToArray());
        return new(ids, dependents);
    }
    private async Task TombstoneKeysAsync(string condition, object value, CancellationToken token)
    {
        var keys = await QueryAsync("SELECT schema_id,scope,scheme,value,thing_id FROM recognition_keys WHERE " + condition,
            reader => (Schema: reader.GetGuid(0), Key: new RecognitionKey(reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), reader.GetString(3)), Thing: Nullable<Guid>(reader, 4)), token, value);
        foreach (var key in keys) await ExecuteAsync("INSERT INTO key_tombstones(key_digest,thing_id) VALUES($1,$2) ON CONFLICT DO NOTHING", token,
            ContentKeys.Recognition(key.Schema, key.Key), Typed(NpgsqlDbType.Uuid, key.Thing));
        await ExecuteAsync("DELETE FROM recognition_keys WHERE " + condition, token, value);
    }
    public async Task<bool> DeleteSchemaAsync(Guid id, CancellationToken token)
    {
        ObservationRules.Require(!(bool)(await ScalarAsync("SELECT EXISTS(SELECT 1 FROM observations WHERE schema_id=$1 UNION ALL SELECT 1 FROM things WHERE schema_id=$1 AND NOT deleted)", token, id))!, "HISTORY-9", "Stored data still uses this schema.");
        var exists = (bool)(await ScalarAsync("SELECT EXISTS(SELECT 1 FROM schemas WHERE schema_id=$1 UNION ALL SELECT 1 FROM thing_types WHERE schema_id=$1)", token, id))!;
        if (!exists) return false;
        await ExecuteAsync("UPDATE write_streams SET schema_revision_ids=schema_revision_ids-$1 WHERE schema_revision_ids ? $1", token, id.ToString("D"));
        await ExecuteAsync("DELETE FROM schemas WHERE schema_id=$1", token, id);
        await ExecuteAsync("DELETE FROM schema_revisions WHERE schema_id=$1", token, id);
        await ExecuteAsync("DELETE FROM thing_types WHERE schema_id=$1", token, id);
        return true;
    }
    public async Task RebuildProjectionsAsync(CancellationToken token)
    {
        foreach (var table in new[] { "schemas", "observation_references", "observation_contents", "replacements", "reviews", "same_as_links", "adoptions" }) await ExecuteAsync($"DELETE FROM {table}", token);
        var revisions = (await ListSchemaRevisionsAsync(null, token)).ToDictionary(revision => revision.SchemaRevisionId);
        foreach (var group in revisions.Values.GroupBy(revision => revision.SchemaId))
            await ExecuteAsync("INSERT INTO schemas(schema_id,resource_name) VALUES($1,$2)", token, group.Key, group.First().Content.ResourceName);
        var observations = await QueryAsync($"SELECT {Columns} FROM observations ORDER BY observation_id", ReadObservation, token);
        foreach (var observation in observations)
            await SaveProjectionsAsync(observation, revisions[observation.SchemaRevisionId], observation.InputSetId is { } id ? await ReadInputSetAsync(id, token) : null, token);
    }
}
