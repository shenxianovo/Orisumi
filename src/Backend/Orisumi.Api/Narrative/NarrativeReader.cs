using System.Text.Json;
using Orisumi.Application.Entities;
using Orisumi.Core;

namespace Orisumi.Api.Narrative;

internal sealed class NarrativeReader(IEntityStore store)
{
    internal Task<NarrativeEvidence> ReadAsync(NarrativeRequest request, CancellationToken token) => store.ExecuteAsync(async (tx, ct) =>
    {
        var interactions = await ReadAll(tx, "assistant-interactions", request, false, ct);
        var periods = await ReadAll(tx, "foreground-application-periods", request, false, ct);
        var previous = await ReadAll(tx, "episodes", request, true, ct);
        var history = await ReadAll(tx, "episodes", request, false, ct);
        var reviews = await ReadAll(tx, "reviews", null, false, ct);
        var inputs = new HashSet<VersionId>();
        var confirmed = new HashSet<Guid>();
        var denials = new List<object>();
        var episodeEntities = history.Select(v => v.EntityId).ToHashSet();
        foreach (var review in reviews)
        {
            var observation = (Observation)review.Content;
            if ((await tx.ReadThingAsync(observation.ObserverId, ct))?.SchemaId != BuiltInSchemas.Person) continue;
            var fields = observation.Fields;
            var target = await tx.ReadVersionAsync(new(fields.GetProperty("targetVersionId").GetGuid()), ct);
            if (target is null || !episodeEntities.Contains(target.EntityId)) continue;
            if (fields.GetProperty("verdict").GetString() == "confirm") confirmed.Add(target.EntityId.Value);
            if (fields.GetProperty("verdict").GetString() != "deny") continue;
            denials.Add(new { episode = NarrativeData.Fields(target), review = fields });
            inputs.Add(review.VersionId); inputs.Add(target.VersionId);
        }
        var sessionIds = interactions.Select(v => NarrativeData.Fields(v).GetProperty("sessionId").GetGuid()).ToHashSet();
        var applicationIds = periods.Select(v => NarrativeData.Fields(v).GetProperty("localApplicationId").GetGuid()).ToHashSet();
        var things = (await tx.ReadThingsAsync(sessionIds.Concat(applicationIds).Select(id => new EntityId(id)).ToArray(), ct)).ToDictionary(t => t.EntityId.Value);
        var sessions = interactions.GroupBy(v => NarrativeData.Fields(v).GetProperty("sessionId").GetGuid()).Select(group =>
        {
            var thing = things.GetValueOrDefault(group.Key);
            if (thing?.NameVersionId is { } name) inputs.Add(name);
            var messages = group.Where(v => NarrativeData.Fields(v).GetProperty("actor").GetString() == "user"
                && NarrativeData.Fields(v).GetProperty("kind").GetString() == "message").Select(v =>
            {
                inputs.Add(v.VersionId);
                var text = MessageText(NarrativeData.Fields(v));
                return new { at = TimeZoneInfo.ConvertTime(((Observation)v.Content).StartAt ?? ((Observation)v.Content).EndAt!.Value, request.Zone()), text };
            }).ToArray();
            var first = NarrativeData.Fields(group.First());
            // The metadata is evidence too, even for sessions with no user message that day.
            inputs.Add(group.First().VersionId);
            return new { entityId = group.Key, name = thing?.Name, product = thing is { Keys.Count: > 0 } ? thing.Keys[0].Scheme : null,
                workingDirectory = first.TryGetProperty("workingDirectory", out var cwd) ? cwd.GetString() : null, messages };
        }).ToArray();
        var applications = periods.Select(v =>
        {
            inputs.Add(v.VersionId);
            var observation = (Observation)v.Content;
            return new { localApplicationId = observation.Fields.GetProperty("localApplicationId").GetGuid(),
                name = observation.Fields.GetProperty("name").GetString(), startAt = observation.StartAt, endAt = observation.EndAt };
        }).ToArray();
        foreach (var item in previous) inputs.Add(item.VersionId);
        return new NarrativeEvidence(new { from = request.From, to = request.To, timeZone = request.TimeZone,
            sessions, applications, previous = previous.Select(v => new { content = NarrativeData.Fields(v), startAt = ((Observation)v.Content).StartAt, endAt = ((Observation)v.Content).EndAt }), denials },
            inputs.ToArray(), previous.ToArray(), confirmed, sessionIds, applicationIds, interactions.Count == 0 && periods.Count == 0);
    }, token);

    internal static string MessageText(JsonElement fields)
    {
        var text = string.Concat(fields.GetProperty("parts").EnumerateArray()
            .Where(p => p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String).Select(p => p.GetProperty("text").GetString()));
        return string.Concat(text.EnumerateRunes().Take(240));
    }

    private static async Task<List<EntityVersion>> ReadAll(IEntityTransaction tx, string resource, NarrativeRequest? range, bool current, CancellationToken token)
    {
        var schema = await tx.FindSchemaAsync(resource, token);
        if (schema is null) return [];
        var result = new List<EntityVersion>();
        var query = new ObservationQuery(range?.From, range?.To, schema, Limit: 1000, Current: current);
        while (true)
        {
            var page = await tx.ListObservationsAsync(query, token);
            result.AddRange(page.Take(1000));
            if (page.Count <= 1000) return result;
            var last = page[999]; var observation = (Observation)last.Content;
            query = query with { After = new(observation.StartAt ?? observation.EndAt, last.EntityId) };
        }
    }
}
