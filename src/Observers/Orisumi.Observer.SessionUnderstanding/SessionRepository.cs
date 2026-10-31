using System.Net;
using System.Text.Json;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.SessionUnderstanding;

internal sealed record SessionRecord(Guid Id, string? Name, Guid? NameVersionId, string? Product,
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
internal sealed record SessionDenial(Guid ReviewVersionId, Guid TargetVersionId, string Kind, JsonElement Content, string? Text);

internal sealed class SessionRepository(DataClient data)
{
    internal async Task<SessionRecord[]> SessionsAsync(CancellationToken token)
    {
        try
        {
            var sessions = await data.ListAsync("things?resourceName=assistant-sessions&limit=1000", token);
            var interactions = (await data.ListAsync("observations?resourceName=assistant-interactions&limit=1000", token))
                .GroupBy(v => SessionRecord.Fields(v).GetProperty("sessionId").GetGuid()).ToDictionary(g => g.Key, g => g.OrderBy(SessionRecord.Time).ToArray());
            return sessions.Select(s => new SessionRecord(s.GetProperty("entityId").GetGuid(), s.GetProperty("name").GetString(),
                s.GetProperty("nameVersionId").ValueKind == JsonValueKind.String ? s.GetProperty("nameVersionId").GetGuid() : null,
                s.GetProperty("keys").EnumerateArray().Select(k => k.GetProperty("scheme").GetString()).FirstOrDefault(),
                interactions.GetValueOrDefault(s.GetProperty("entityId").GetGuid()) ?? [])).ToArray();
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound) { return []; }
    }

    internal async Task<SessionDenial[]> DenialsAsync(Guid session, CancellationToken token)
    {
        var people = (await data.ListAsync("things?resourceName=people&limit=1000", token)).Select(v => v.GetProperty("entityId").GetGuid()).ToHashSet();
        var nameSchemas = (await data.GetAsync($"entities/{CommonSchemas.Name}/versions", token)).GetProperty("versions").EnumerateArray().Select(v => v.GetProperty("versionId").GetGuid()).ToHashSet();
        var relationSchemas = (await data.GetAsync($"entities/{CommonSchemas.Relation}/versions", token)).GetProperty("versions").EnumerateArray().Select(v => v.GetProperty("versionId").GetGuid()).ToHashSet();
        var reviews = (await data.ListAsync("observations?resourceName=reviews&limit=1000", token))
            .Where(v => people.Contains(v.GetProperty("observerId").GetGuid()) && SessionRecord.Fields(v).GetProperty("verdict").GetString() == "deny").ToArray();
        var targets = new Dictionary<Guid, JsonElement>();
        foreach (var chunk in reviews.Select(v => SessionRecord.Fields(v).GetProperty("targetVersionId").GetGuid()).Distinct().Chunk(256))
            foreach (var result in (await data.VersionsAsync(chunk, token)).GetProperty("results").EnumerateArray())
                if (result.GetProperty("status").GetString() == "found") targets[result.GetProperty("versionId").GetGuid()] = result.GetProperty("version");
        var denials = new List<SessionDenial>();
        foreach (var review in reviews)
        {
            var fields = SessionRecord.Fields(review); var targetId = fields.GetProperty("targetVersionId").GetGuid();
            if (!targets.TryGetValue(targetId, out var target) || !target.TryGetProperty("schemaVersionId", out var schema)) continue;
            var content = SessionRecord.Fields(target);
            var kind = nameSchemas.Contains(schema.GetGuid()) ? "name" : relationSchemas.Contains(schema.GetGuid()) ? "relation" : null;
            if (kind is null || content.GetProperty(kind == "name" ? "thingId" : "subjectId").GetGuid() != session) continue;
            denials.Add(new(review.GetProperty("versionId").GetGuid(), targetId, kind, content, fields.GetProperty("text").GetString()));
        }
        return denials.ToArray();
    }
}
