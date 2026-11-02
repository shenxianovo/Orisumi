using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Orisumi.Application.Data;
using Orisumi.Core;

namespace Orisumi.Infrastructure.Database;

internal sealed partial class PostgresDataTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction) : IDataTransaction
{
    private const string Columns = "observation_id, schema_revision_id, observer_id, speaker_id, observer_description_id, formation, input_set_id, replaces, start_at, end_at, time_zone, recorded_at, content";
    public async Task<T> SavepointAsync<T>(Func<Task<T>> operation, CancellationToken token)
    {
        await transaction.SaveAsync("observation_write", token);
        try { var result = await operation(); await transaction.ReleaseAsync("observation_write", token); return result; }
        catch (Exception error) when (error is ModelViolationException or WriteSkippedException)
        { await transaction.RollbackAsync("observation_write", token); await transaction.ReleaseAsync("observation_write", token); throw; }
    }
    public async Task<bool> IsHostInstanceAsync(Guid id, Guid host, CancellationToken token) => (bool)(await ScalarAsync("SELECT EXISTS(SELECT 1 FROM observer_instances WHERE thing_id=$1 AND host_id=$2 AND NOT removed)", token, id, host))!;
    public async Task<bool> ThingExistsAsync(Guid id, CancellationToken token) => (bool)(await ScalarAsync("SELECT EXISTS(SELECT 1 FROM things WHERE thing_id=$1 UNION ALL SELECT 1 FROM observer_instances WHERE thing_id=$1)", token, id))!;
    public async Task<bool> IsThingDeletedAsync(Guid id, CancellationToken token) => (bool)(await ScalarAsync("SELECT EXISTS(SELECT 1 FROM things WHERE thing_id=$1 AND deleted UNION ALL SELECT 1 FROM observer_instances WHERE thing_id=$1 AND removed)", token, id))!;
    public async Task<bool> IsObservationDeletedAsync(Guid id, CancellationToken token) => (bool)(await ScalarAsync("SELECT EXISTS(SELECT 1 FROM deleted_observations WHERE observation_id=$1)", token, id))!;
    public Task CreateThingAsync(Guid id, Guid schemaId, CancellationToken token) => ExecuteAsync("INSERT INTO things(thing_id,schema_id,deleted) VALUES($1,$2,false)", token, id, schemaId);
    public async Task<Thing?> ReadThingAsync(Guid id, CancellationToken token) => (await ReadThingsAsync("WHERE thing_id=$1 AND NOT deleted", token, id)).SingleOrDefault();
    public Task<IReadOnlyList<Thing>> ListThingsAsync(Guid? schema, CancellationToken token) => schema is { } id
        ? ReadThingsAsync("WHERE schema_id=$1 AND NOT deleted ORDER BY thing_id", token, id) : ReadThingsAsync("WHERE NOT deleted ORDER BY thing_id", token);
    private async Task<IReadOnlyList<Thing>> ReadThingsAsync(string where, CancellationToken token, params object[] values)
    {
        return await QueryAsync("SELECT t.thing_id,t.schema_id,COALESCE((SELECT jsonb_agg(jsonb_build_object('scope',scope,'scheme',scheme,'value',value)) FROM recognition_keys k WHERE k.thing_id=t.thing_id),'[]') FROM things t " + where,
            reader => new Thing(reader.GetGuid(0), reader.GetGuid(1), JsonSerializer.Deserialize<RecognitionKey[]>(reader.GetString(2), ObservationJson.Options)!), token, values);
    }
    public async Task<Recognition?> FindRecognitionAsync(Guid schema, RecognitionKey key, CancellationToken token) =>
        (await QueryAsync("SELECT thing_id,latest_observation_id FROM recognition_keys WHERE schema_id=$1 AND scope IS NOT DISTINCT FROM $2 AND scheme=$3 AND value=$4",
            reader => new Recognition(Nullable<Guid>(reader, 0), Nullable<Guid>(reader, 1)), token, schema, Typed(NpgsqlDbType.Text, key.Scope), key.Scheme, key.Value)).SingleOrDefault();
    public async Task<KeyTombstone?> FindTombstoneAsync(Guid schema, RecognitionKey key, CancellationToken token) =>
        (await QueryAsync("SELECT thing_id FROM key_tombstones WHERE key_digest=$1", reader => new KeyTombstone(Nullable<Guid>(reader, 0)), token, ContentKeys.Recognition(schema, key))).SingleOrDefault();
    public Task RegisterRecognitionAsync(Guid schema, RecognitionKey key, Recognition target, CancellationToken token) =>
        ExecuteAsync("INSERT INTO recognition_keys(schema_id,scope,scheme,value,thing_id,latest_observation_id) VALUES($1,$2,$3,$4,$5,$6) ON CONFLICT(schema_id,scope,scheme,value) DO UPDATE SET latest_observation_id=excluded.latest_observation_id",
            token, schema, Typed(NpgsqlDbType.Text, key.Scope), key.Scheme, key.Value, Typed(NpgsqlDbType.Uuid, target.ThingId), Typed(NpgsqlDbType.Uuid, target.LatestObservationId));
    public async Task<WriteStreamState> ReadStreamAsync(Guid observer, CancellationToken token) => (await QueryAsync("SELECT last_sequence,schema_revision_ids,description_id FROM write_streams WHERE observer_id=$1",
        reader => new WriteStreamState(reader.GetInt64(0), JsonSerializer.Deserialize<Dictionary<Guid, Guid>>(reader.GetString(1))!, Nullable<Guid>(reader, 2)), token, observer)).SingleOrDefault() ?? new(0, []);
    public Task SaveStreamAsync(Guid observer, WriteStreamState stream, CancellationToken token) => ExecuteAsync("INSERT INTO write_streams(observer_id,last_sequence,schema_revision_ids,description_id) VALUES($1,$2,$3,$4) ON CONFLICT(observer_id) DO UPDATE SET last_sequence=excluded.last_sequence,schema_revision_ids=excluded.schema_revision_ids,description_id=excluded.description_id",
        token, observer, stream.LastSequence, Typed(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(stream.SchemaRevisionIds)), Typed(NpgsqlDbType.Uuid, stream.DescriptionId));
    public async Task<SchemaRevision?> ReadSchemaRevisionAsync(Guid id, CancellationToken token) =>
        (await ReadRevisionsAsync("WHERE schema_revision_id=$1", token, id)).SingleOrDefault();
    public Task<IReadOnlyList<SchemaRevision>> ListSchemaRevisionsAsync(Guid? schema, CancellationToken token) => schema is { } id
        ? ReadRevisionsAsync("WHERE schema_id=$1 ORDER BY schema_revision_id", token, id) : ReadRevisionsAsync("ORDER BY schema_revision_id", token);
    private Task<IReadOnlyList<SchemaRevision>> ReadRevisionsAsync(string where, CancellationToken token, params object[] values) =>
        QueryAsync("SELECT schema_revision_id,schema_id,recorded_at,content FROM schema_revisions " + where,
            reader => new SchemaRevision(reader.GetGuid(0), reader.GetGuid(1), reader.GetFieldValue<DateTimeOffset>(2), JsonSerializer.Deserialize<Schema>(reader.GetString(3), ObservationJson.Options)!), token, values);
    public Task<IReadOnlyList<ThingType>> ListThingTypesAsync(CancellationToken token) => QueryAsync("SELECT schema_id,display_name,description,resource_name FROM thing_types ORDER BY schema_id",
        reader => new ThingType(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)), token);
    public async Task SaveSchemaRevisionAsync(SchemaRevision revision, CancellationToken token)
    {
        await ExecuteAsync("INSERT INTO schema_revisions(schema_revision_id,schema_id,recorded_at,content) VALUES($1,$2,$3,$4)", token,
            revision.SchemaRevisionId, revision.SchemaId, revision.RecordedAt, Typed(NpgsqlDbType.Jsonb, ObservationJson.Content(revision.Content).GetRawText()));
        await ExecuteAsync("INSERT INTO schemas(schema_id,resource_name) VALUES($1,$2) ON CONFLICT DO NOTHING", token, revision.SchemaId, revision.Content.ResourceName);
    }
    public Task SaveThingTypeAsync(ThingType type, CancellationToken token) => ExecuteAsync("INSERT INTO thing_types(schema_id,display_name,description,resource_name) VALUES($1,$2,$3,$4) ON CONFLICT(schema_id) DO UPDATE SET display_name=excluded.display_name,description=excluded.description",
        token, type.SchemaId, type.DisplayName, type.Description, type.ResourceName);
    public async Task<Guid?> FindSchemaAsync(string resource, CancellationToken token) => await ScalarAsync("SELECT schema_id FROM schemas WHERE resource_name=$1 UNION ALL SELECT schema_id FROM thing_types WHERE resource_name=$1", token, resource) as Guid?;
    public async Task<Observation?> ReadObservationAsync(Guid id, CancellationToken token) => (await QueryAsync($"SELECT {Columns} FROM observations WHERE observation_id=$1", ReadObservation, token, id)).SingleOrDefault();
    public Task<IReadOnlyList<Observation>> ReadObservationsAsync(IReadOnlyList<Guid> ids, CancellationToken token) => QueryAsync($"SELECT {Columns} FROM observations WHERE observation_id=ANY($1)", ReadObservation, token, (object)ids.ToArray());
    public async Task<IReadOnlyList<Observation>> ReadControlsAsync(CancellationToken token)
    {
        const string columns = "o.observation_id,o.schema_revision_id,o.observer_id,o.speaker_id,o.observer_description_id,o.formation,o.input_set_id,o.replaces,o.start_at,o.end_at,o.time_zone,o.recorded_at,";
        var reviews = await QueryAsync("SELECT " + columns + """
            jsonb_build_object('verdict',r.verdict,'targetId',r.target_id,'scope',CASE WHEN r.target_id IS NULL THEN jsonb_build_object('thingIds',r.scope_thing_ids,'schemaIds',r.scope_schema_ids) ELSE NULL END)
            FROM reviews r JOIN observations o ON o.observation_id=r.observation_id
            """, ReadObservation, token);
        var links = await QueryAsync("SELECT " + columns + "jsonb_build_object('leftId',l.left_id,'rightId',l.right_id) FROM same_as_links l JOIN observations o ON o.observation_id=l.observation_id", ReadObservation, token);
        var adoptions = await QueryAsync("SELECT " + columns + "jsonb_build_object('schemaId',a.schema_id,'observerDescriptionId',a.observer_description_id) FROM adoptions a JOIN observations o ON o.observation_id=a.observation_id", ReadObservation, token);
        return reviews.Concat(links).Concat(adoptions).ToArray();
    }
    private static Observation ReadObservation(NpgsqlDataReader reader) => new(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), Nullable<Guid>(reader, 3), Nullable<Guid>(reader, 4), reader.GetString(5),
        Nullable<Guid>(reader, 6), reader.GetFieldValue<Guid[]>(7), Nullable<DateTimeOffset>(reader, 8), Nullable<DateTimeOffset>(reader, 9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetFieldValue<DateTimeOffset>(11), JsonSerializer.Deserialize<JsonElement>(reader.GetString(12)));
    public async Task<InputSet?> ReadInputSetAsync(Guid id, CancellationToken token) =>
        (await QueryAsync("SELECT input_set_id,members FROM input_sets WHERE input_set_id=$1", reader => new InputSet(reader.GetGuid(0), reader.GetFieldValue<Guid[]>(1)), token, id)).SingleOrDefault();
    public Task<IReadOnlyList<InputSet>> ReadInputSetsAsync(IReadOnlyList<Guid> ids, CancellationToken token) =>
        QueryAsync("SELECT input_set_id,members FROM input_sets WHERE input_set_id=ANY($1)", reader => new InputSet(reader.GetGuid(0), reader.GetFieldValue<Guid[]>(1)), token, (object)ids.ToArray());
    public Task SaveInputSetAsync(InputSet input, CancellationToken token) => ExecuteAsync("INSERT INTO input_sets(input_set_id,members) VALUES($1,$2)", token, input.InputSetId, input.Members);
    public async Task<Guid> CreateRunAsync(DateTimeOffset at, CancellationToken token)
    { var id = Guid.CreateVersion7(); await ExecuteAsync("INSERT INTO runs(run_id,created_at) VALUES($1,$2)", token, id, ObservationTime.Normalize(at)); return id; }
    public async Task<Guid[]?> ReadRunAsync(Guid run, CancellationToken token) => await ScalarAsync("SELECT ARRAY(SELECT observation_id FROM run_reads WHERE run_id=$1) FROM runs WHERE run_id=$1", token, run) as Guid[];
    public Task RecordReadsAsync(Guid run, IEnumerable<Guid> observations, CancellationToken token) => ExecuteAsync("INSERT INTO run_reads(run_id,observation_id) SELECT $1,unnest($2::uuid[]) ON CONFLICT DO NOTHING", token, run, observations.ToArray());

    private async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, Func<NpgsqlDataReader, T> read, CancellationToken token, params object[] values)
    {
        await using var command = Command(sql, values); await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<T>(); while (await reader.ReadAsync(token)) result.Add(read(reader)); return result;
    }
    private async Task<object?> ScalarAsync(string sql, CancellationToken token, params object[] values)
    { await using var command = Command(sql, values); return await command.ExecuteScalarAsync(token); }
    private async Task ExecuteAsync(string sql, CancellationToken token, params object[] values)
    { await using var command = Command(sql, values); await command.ExecuteNonQueryAsync(token); }
    private NpgsqlCommand Command(string sql, object[] values)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values) command.Parameters.Add(value is NpgsqlParameter parameter ? parameter : new NpgsqlParameter { Value = value });
        return command;
    }
    private static NpgsqlParameter Typed(NpgsqlDbType type, object? value) => new() { NpgsqlDbType = type, Value = value ?? DBNull.Value };
    private static T? Nullable<T>(NpgsqlDataReader reader, int ordinal) where T : struct => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<T>(ordinal);
}
