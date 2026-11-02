using System.Text.Json;
using Orisumi.Application.Data;
using Orisumi.Core;

namespace Orisumi.Api.Narrative;

internal sealed class NarrativeReader(DataService data)
{
    internal async Task<NarrativeEvidence> ReadAsync(NarrativeRequest request, CancellationToken token)
    {
        var interactions = await ReadAll("assistant-interactions", request, true, token);
        var periods = await ReadAll("foreground-application-periods", request, true, token);
        var previous = await ReadAll("episodes", request, true, token);
        var reviews = await ReadAll("reviews", null, true, token);
        var inputs = new HashSet<Guid>();
        var denials = reviews.Where(review => review.Formation == "stated" && review.Content.GetProperty("verdict").GetString() == "deny").ToArray();
        foreach (var review in denials) inputs.Add(review.ObservationId);
        var sessionIds = interactions.Select(item => item.Content.GetProperty("sessionId").GetGuid()).ToHashSet();
        var applicationIds = periods.Select(item => item.Content.GetProperty("localApplicationId").GetGuid()).ToHashSet();
        var things = (await data.ListThingsAsync(cancellationToken: token)).ToDictionary(thing => thing.ThingId);
        var sessions = interactions.GroupBy(item => item.Content.GetProperty("sessionId").GetGuid()).Select(group =>
        {
            var thing = things.GetValueOrDefault(group.Key);
            if (thing?.NameObservationId is { } name) inputs.Add(name);
            var messages = group.Where(item => item.Content.GetProperty("actor").GetString() == "user" && item.Content.GetProperty("kind").GetString() == "message").Select(item =>
            {
                inputs.Add(item.ObservationId);
                return new { at = TimeZoneInfo.ConvertTime((item.StartAt ?? item.EndAt)!.Value, request.Zone()), text = MessageText(item.Content) };
            }).ToArray();
            var first = group.First(); inputs.Add(first.ObservationId);
            return new { thingId = group.Key, name = thing?.Name, product = thing is { Keys.Count: > 0 } ? thing.Keys[0].Scheme : null,
                workingDirectory = first.Content.TryGetProperty("workingDirectory", out var cwd) ? cwd.GetString() : null, messages };
        }).ToArray();
        var applications = periods.Select(item =>
        {
            inputs.Add(item.ObservationId);
            return new { localApplicationId = item.Content.GetProperty("localApplicationId").GetGuid(), name = item.Content.GetProperty("name").GetString(), startAt = item.StartAt, endAt = item.EndAt };
        }).ToArray();
        foreach (var item in previous) inputs.Add(item.ObservationId);
        return new(new { from = request.From, to = request.To, timeZone = request.TimeZone, sessions, applications,
            previous = previous.Select(item => new { content = item.Content, startAt = item.StartAt, endAt = item.EndAt }), denials = denials.Select(item => item.Content) },
            inputs.ToArray(), previous.ToArray(), sessionIds, applicationIds, interactions.Count == 0 && periods.Count == 0);
    }
    internal static string MessageText(JsonElement fields) => string.Concat(string.Concat(fields.GetProperty("parts").EnumerateArray()
        .Where(part => part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String).Select(part => part.GetProperty("text").GetString())).EnumerateRunes().Take(240));
    private async Task<IReadOnlyList<Observation>> ReadAll(string resource, NarrativeRequest? range, bool current, CancellationToken token)
    {
        try { return await data.ListObservationsAsync(new(range?.From, range?.To, Current: current), resource, cancellationToken: token); }
        catch (ResourceNotFoundException) { return []; }
    }
}
