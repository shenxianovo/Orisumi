using System.Net;
using System.Text.Json;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.SessionUnderstanding;

internal sealed record SessionRecord(Guid Id, string? Name, Guid? NameObservationId, string? Product,
    JsonElement[] Interactions)
{
    internal DateTimeOffset? Last => Interactions.Select(Time).Max();
    internal JsonElement[] Messages => Interactions.Where(v => Fields(v).GetProperty("actor").GetString() == "user"
        && Fields(v).GetProperty("kind").GetString() == "message" && Text(v).Length > 0).ToArray();
    internal static DateTimeOffset? Time(JsonElement v) => v.GetProperty("endAt").ValueKind == JsonValueKind.String
        ? v.GetProperty("endAt").GetDateTimeOffset() : v.GetProperty("startAt").ValueKind == JsonValueKind.String ? v.GetProperty("startAt").GetDateTimeOffset() : null;
    internal static JsonElement Fields(JsonElement v) => v.GetProperty("content");
    internal static string Text(JsonElement v) => string.Concat(string.Concat(Fields(v).GetProperty("parts").EnumerateArray()
        .Where(p => p.GetProperty("text").ValueKind == JsonValueKind.String).Select(p => p.GetProperty("text").GetString())).EnumerateRunes().Take(240));
}
internal sealed record SessionDenial(Guid ReviewObservationId, Guid TargetObservationId, string Kind, JsonElement Content, string? Text);

internal sealed class SessionRepository(DataClient data)
{
    internal async Task<SessionRecord[]> SessionsAsync(CancellationToken token)
    {
        try
        {
            var sessions = await data.ListAsync("things?resourceName=assistant-sessions&limit=1000", token);
            var interactions = (await data.ListAsync("observations?resourceName=assistant-interactions&limit=1000", token))
                .GroupBy(v => SessionRecord.Fields(v).GetProperty("sessionId").GetGuid()).ToDictionary(g => g.Key, g => g.OrderBy(SessionRecord.Time).ToArray());
            return sessions.Select(s => new SessionRecord(s.GetProperty("thingId").GetGuid(), s.GetProperty("name").GetString(),
                s.GetProperty("nameObservationId").ValueKind == JsonValueKind.String ? s.GetProperty("nameObservationId").GetGuid() : null,
                s.GetProperty("keys").EnumerateArray().Select(k => k.GetProperty("scheme").GetString()).FirstOrDefault(),
                interactions.GetValueOrDefault(s.GetProperty("thingId").GetGuid()) ?? [])).ToArray();
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound) { return []; }
    }

    internal async Task<SessionDenial[]> DenialsAsync(Guid session, CancellationToken token)
    {
        var nameSchemas = (await data.ListAsync("schema-revisions?resourceName=names", token)).Select(v => v.GetProperty("schemaRevisionId").GetGuid()).ToHashSet();
        var relationSchemas = (await data.ListAsync("schema-revisions?resourceName=relations", token)).Select(v => v.GetProperty("schemaRevisionId").GetGuid()).ToHashSet();
        var reviews = (await data.ListAsync("observations?resourceName=reviews&limit=1000", token))
            .Where(v => v.GetProperty("formation").GetString() == "stated" && SessionRecord.Fields(v).GetProperty("targetId").ValueKind == JsonValueKind.String && SessionRecord.Fields(v).GetProperty("verdict").GetString() == "deny").ToArray();
        var targets = new Dictionary<Guid, JsonElement>();
        foreach (var chunk in reviews.Select(v => SessionRecord.Fields(v).GetProperty("targetId").GetGuid()).Distinct().Chunk(256))
            foreach (var result in (await data.ReadObservationsAsync(chunk, token)).GetProperty("results").EnumerateArray())
                if (result.GetProperty("status").GetString() == "found") targets[result.GetProperty("observationId").GetGuid()] = result.GetProperty("observation");
        var denials = new List<SessionDenial>();
        foreach (var review in reviews)
        {
            var fields = SessionRecord.Fields(review); var targetId = fields.GetProperty("targetId").GetGuid();
            if (!targets.TryGetValue(targetId, out var target) || !target.TryGetProperty("schemaRevisionId", out var schema)) continue;
            var content = SessionRecord.Fields(target);
            var kind = nameSchemas.Contains(schema.GetGuid()) ? "name" : relationSchemas.Contains(schema.GetGuid()) ? "relation" : null;
            if (kind is null || content.GetProperty(kind == "name" ? "thingId" : "subjectId").GetGuid() != session) continue;
            denials.Add(new(review.GetProperty("observationId").GetGuid(), targetId, kind, content, fields.GetProperty("text").GetString()));
        }
        return denials.ToArray();
    }
}
