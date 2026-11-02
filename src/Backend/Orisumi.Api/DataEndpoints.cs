using System.Globalization;
using System.Text.Json;
using Orisumi.Application.Data;
using Orisumi.Application.Management;
using Orisumi.Core;

namespace Orisumi.Api;

internal static class DataEndpoints
{
    internal static void MapData(this WebApplication app)
    {
        app.MapPost("/writes", WriteAsync).WithName("write");
        app.MapGet("/observations", (HttpRequest request, DataService service, CancellationToken token) => ListAsync(request, service, false, token)).WithName("listObservations");
        app.MapGet("/search", (HttpRequest request, DataService service, CancellationToken token) => ListAsync(request, service, true, token)).WithName("searchObservations");
        app.MapGet("/observations/{observationId}", async (string observationId, HttpRequest request, DataService service, CancellationToken token) =>
            await service.ReadObservationAsync(Id(observationId), Run(request), token) is { } observation ? Results.Json(observation) : ApiProblem.Create(404, "Unknown observationId.")).WithName("getObservation");
        app.MapPost("/observations/read", ReadObservationsAsync).WithName("readObservations");
        app.MapGet("/input-sets/{inputSetId}", async (string inputSetId, DataService service, CancellationToken token) =>
            await service.ReadInputSetAsync(Id(inputSetId), token) is { } input ? Results.Json(input) : ApiProblem.Create(404, "Unknown inputSetId.")).WithName("getInputSet");
        app.MapPost("/runs", async (DataService service, CancellationToken token) => Results.Json(new { runId = await service.CreateRunAsync(token) }, statusCode: 201)).WithName("createRun");
        app.MapGet("/things", ListThingsAsync).WithName("listThings");
        app.MapGet("/things/{thingId}", async (string thingId, HttpRequest request, DataService service, CancellationToken token) =>
            await service.ReadThingAsync(Id(thingId), Run(request), token) is { } thing ? Results.Json(thing) : ApiProblem.Create(404, "Unknown thingId.")).WithName("getThing");
        app.MapPost("/things/read", ReadThingsAsync).WithName("readThings");
        app.MapGet("/schema-revisions", ListSchemasAsync).WithName("listSchemaRevisions");
        app.MapGet("/schema-revisions/{schemaRevisionId}", async (string schemaRevisionId, DataService service, CancellationToken token) =>
            await service.ReadSchemaRevisionAsync(Id(schemaRevisionId), token) is { } revision ? Results.Json(revision) : ApiProblem.Create(404, "Unknown schemaRevisionId.")).WithName("getSchemaRevision");
        app.MapGet("/thing-types", async (DataService service, CancellationToken token) => Results.Json(new { thingTypes = await service.ListThingTypesAsync(token) })).WithName("listThingTypes");
        app.MapGet("/things/{thingId}/referrers", (string thingId, HttpRequest request, DataService service, CancellationToken token) => ReferrersAsync(Id(thingId), true, request, service, token)).WithName("listThingReferrers");
        app.MapGet("/observations/{observationId}/referrers", (string observationId, HttpRequest request, DataService service, CancellationToken token) => ReferrersAsync(Id(observationId), false, request, service, token)).WithName("listObservationReferrers");
        app.MapDelete("/observations/{observationId}", async (string observationId, DataService service, CancellationToken token) =>
            await service.DeleteObservationAsync(Id(observationId), token) is { } result ? Results.Json(result) : ApiProblem.Create(404, "Unknown observationId.")).WithName("deleteObservation");
        app.MapDelete("/things/{thingId}", async (string thingId, DataService service, RuntimeCoordinator coordinator, CancellationToken token) =>
        { var result = await service.DeleteThingAsync(Id(thingId), token); coordinator.Notify(); return result is null ? ApiProblem.Create(404, "Unknown thingId.") : Results.Json(result); }).WithName("deleteThing");
        app.MapDelete("/schemas/{schemaId}", async (string schemaId, DataService service, CancellationToken token) =>
            await service.DeleteSchemaAsync(Id(schemaId), token) ? Results.NoContent() : ApiProblem.Create(404, "Unknown schemaId.")).WithName("deleteSchema");
    }

    private static async Task<IResult> WriteAsync(HttpRequest request, WriteService service, CancellationToken token)
    {
        if (request.ContentLength > 10 * 1024 * 1024) throw new BadHttpRequestException("Request exceeds 10 MiB.", 413);
        if (!request.HasJsonContentType()) throw new JsonException("Content-Type must be application/json.");
        using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: token);
        var write = WriteContract.Read(body.RootElement);
        Guid? host = request.HttpContext.Items.TryGetValue("ObserverHostId", out var owner) ? (Guid)owner! : null;
        var result = await service.WriteAsync(write, host, token);
        var results = result.Results.Select<WriteResult, object>(item => item.Error is { } error ? new { error }
            : item.Skipped ? new { skipped = true, changed = false, replaced = Array.Empty<Guid>() }
            : new { observation = item.Observation!, changed = item.Changed, replaced = item.Replaced }).ToArray();
        return result.Duplicate ? Results.Json(new { duplicate = true, results }) : Results.Json(new { results });
    }
    private static async Task<IResult> ListAsync(HttpRequest request, DataService service, bool search, CancellationToken token)
    {
        var fromText = Parameter(request, "from"); var toText = Parameter(request, "to");
        if ((fromText is null) != (toText is null)) throw new JsonException("from and to must be supplied together.");
        DateTimeOffset? from = fromText is null ? null : ObservationJson.ParseTime(fromText);
        DateTimeOffset? to = toText is null ? null : ObservationJson.ParseTime(toText);
        if (from >= to) throw new JsonException("to must be later than from.");
        var formation = Parameter(request, "formation");
        if (formation is not null && !ObservationRules.ValidFormation(formation)) throw new JsonException("Invalid formation.");
        var role = Parameter(request, "role"); var thing = OptionalId(request, "thingId");
        if (role is not (null or "about" or "part" or "mention") || role is not null && thing is null) throw new JsonException("role requires thingId and a declared role.");
        var text = search ? Parameter(request, "q") ?? throw new JsonException("q is required.") : null;
        if (text is "") throw new JsonException("q must not be empty.");
        var query = new ObservationQuery(from, to, ObserverId: OptionalId(request, "observerId"), Formation: formation, Current: Current(request), Descending: Descending(request),
            ThingId: thing, Role: role, SpeakerId: OptionalId(request, "speakerId"), ExcludeSpeakerType: Parameter(request, "excludeSpeakerType"), Search: text);
        string? next = null;
        var observations = await service.ListObservationsAsync(query, Parameter(request, "resourceName"), cancellationToken: token, page: records =>
        {
            var selected = Slice(request, records, record => record.ObservationId, record => record.StartAt ?? record.EndAt);
            next = selected.Next; return selected.Items;
        });
        await service.RecordReadsAsync(Run(request), observations.Select(observation => observation.ObservationId), token);
        return Results.Json(new { observations, next });
    }
    private static async Task<IResult> ListThingsAsync(HttpRequest request, DataService service, CancellationToken token)
    {
        var things = await service.ListThingsAsync(Parameter(request, "resourceName"), token);
        var ordered = Descending(request) ? things.OrderByDescending(thing => thing.ThingId) : things.OrderBy(thing => thing.ThingId);
        var page = Slice(request, ordered.ToArray(), thing => thing.ThingId);
        return Results.Json(new { things = page.Items, next = page.Next });
    }
    private static async Task<IResult> ListSchemasAsync(HttpRequest request, DataService service, CancellationToken token)
    {
        var revisions = await service.ListSchemaRevisionsAsync(Parameter(request, "resourceName"), token);
        var ordered = Descending(request) ? revisions.OrderByDescending(revision => revision.SchemaRevisionId) : revisions.OrderBy(revision => revision.SchemaRevisionId);
        var page = Slice(request, ordered.ToArray(), schema => schema.SchemaRevisionId);
        return Results.Json(new { schemaRevisions = page.Items, next = page.Next });
    }
    private static async Task<IResult> ReferrersAsync(Guid id, bool thing, HttpRequest request, DataService service, CancellationToken token)
    {
        var referrers = await service.ListReferrersAsync(id, thing, Current(request), Parameter(request, "path"), Parameter(request, "resourceName"), token);
        if (referrers is null) return ApiProblem.Create(404, "Unknown target.");
        var ordered = Descending(request) ? referrers.OrderByDescending(item => item.Observation.ObservationId) : referrers.OrderBy(item => item.Observation.ObservationId);
        var page = Slice(request, ordered.ToArray(), item => item.Observation.ObservationId);
        await service.RecordReadsAsync(Run(request), page.Items.Select(item => item.Observation.ObservationId), token);
        return Results.Json(new { referrers = page.Items.Select(item => new { observation = item.Observation, references = item.References.Select(reference => new { path = reference.Path, targetId = reference.TargetId }) }), next = page.Next });
    }
    private static async Task<IResult> ReadObservationsAsync(JsonElement body, HttpRequest request, IDataStore store, DataService service, CancellationToken token)
    {
        var ids = ReadIds(body, "observationIds");
        var found = new List<Guid>();
        var results = await store.ExecuteAsync(async (tx, ct) =>
        {
            var observations = (await tx.ReadObservationsAsync(ids, ct)).ToDictionary(observation => observation.ObservationId);
            var result = new List<object>();
            foreach (var id in ids)
            {
                if (observations.TryGetValue(id, out var observation)) { result.Add(new { observationId = id, status = "found", observation }); found.Add(id); }
                else result.Add(new { observationId = id, status = await tx.IsObservationDeletedAsync(id, ct) ? "deleted" : "not_found" });
            }
            return result;
        }, token);
        await service.RecordReadsAsync(Run(request), found, token);
        return Results.Json(new { results });
    }
    private static async Task<IResult> ReadThingsAsync(JsonElement body, IDataStore store, DataService service, CancellationToken token)
    {
        var ids = ReadIds(body, "thingIds");
        var things = (await service.ListThingsAsync(cancellationToken: token)).ToDictionary(thing => thing.ThingId);
        var results = await store.ExecuteAsync(async (tx, ct) =>
        {
            var result = new List<object>();
            foreach (var id in ids) result.Add(things.TryGetValue(id, out var thing) ? new { thingId = id, status = "found", thing }
                : (object)new { thingId = id, status = await tx.IsThingDeletedAsync(id, ct) ? "deleted" : "not_found" });
            return result;
        }, token);
        return Results.Json(new { results });
    }
    private static Guid[] ReadIds(JsonElement body, string member)
    {
        if (body.ValueKind != JsonValueKind.Object || body.EnumerateObject().Count() != 1 || !body.TryGetProperty(member, out var list)
            || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() is < 1 or > 256) throw new JsonException("Expected between 1 and 256 IDs.");
        return list.EnumerateArray().Select(value => Id(value.GetString()!)).ToArray();
    }
    private static (T[] Items, string? Next) Slice<T>(HttpRequest request, IReadOnlyList<T> items, Func<T, Guid> id, Func<T, DateTimeOffset?>? time = null)
    {
        var kind = QueryKind(request.Path, request.Query.Where(pair => pair.Key is not ("after" or "limit" or "runId")).OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value).ToArray());
        var descending = Descending(request);
        IEnumerable<T> remaining = items;
        if (Parameter(request, "after") is { } after)
        {
            var parts = Decode(after, kind).Split('|');
            var cursor = Id(parts[0]);
            DateTimeOffset? point = parts.Length == 2 && parts[1].Length > 0 ? ObservationJson.ParseTime(parts[1]) : null;
            remaining = items.Where(item =>
            {
                if (time is null) return descending ? id(item).CompareTo(cursor) < 0 : id(item).CompareTo(cursor) > 0;
                var at = time(item);
                if (at == point) return id(item).CompareTo(cursor) > 0;
                if (at is null) return point is not null;
                return point is not null && (descending ? at < point : at > point);
            });
        }
        var selected = remaining.Take(Limit(request) + 1).ToArray();
        var page = selected.Take(Limit(request)).ToArray();
        var last = page.LastOrDefault();
        return (page, selected.Length > page.Length ? Encode(kind, id(last!).ToString("D") + "|" + (time?.Invoke(last!) is { } at ? ObservationTime.Format(at) : "")) : null);
    }
    private static Guid? OptionalId(HttpRequest request, string name) => Parameter(request, name) is { } id ? Id(id) : null;
    private static Guid? Run(HttpRequest request) => OptionalId(request, "runId");
    private static bool Current(HttpRequest request) => (Parameter(request, "view") ?? "current") switch { "current" => true, "all" => false, _ => throw new JsonException("view must be current or all.") };
    private static bool Descending(HttpRequest request) => (Parameter(request, "order") ?? "asc") switch { "asc" => false, "desc" => true, _ => throw new JsonException("order must be asc or desc.") };

    internal static string? Parameter(HttpRequest request, string name)
    {
        if (!request.Query.TryGetValue(name, out var values)) return null;
        if (values.Count != 1 || values[0]!.Contains('\0')) throw new JsonException($"Invalid parameter {name}.");
        return values[0];
    }
    private static int Limit(HttpRequest request)
    {
        var value = Parameter(request, "limit");
        if (value is null) return 100;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) || limit is < 1 or > 1000)
            throw new JsonException("limit must be between 1 and 1000.");
        return limit;
    }
    internal static Guid Id(string value) => Guid.TryParseExact(value, "D", out var id) ? id : throw new JsonException("ID must be a UUID.");
    private static string QueryKind(string kind, params string?[] values) => kind + ":" + Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(values)));
    private static string Encode(string kind, string value) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(kind + "|" + value));
    private static string Decode(string cursor, string kind)
    {
        string value;
        try { value = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor)); }
        catch (FormatException exception) { throw new JsonException("Invalid cursor.", exception); }
        return value.StartsWith(kind + "|", StringComparison.Ordinal) ? value[(kind.Length + 1)..] : throw new JsonException("Invalid cursor kind.");
    }
}
