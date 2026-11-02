using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orisumi.Application.Data;
using Orisumi.Core;

namespace Orisumi.Api.Narrative;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record NarrativeRequest([property: JsonRequired] DateTimeOffset From, [property: JsonRequired] DateTimeOffset To, [property: JsonRequired] string TimeZone)
{
    internal TimeZoneInfo Zone()
    {
        if (To <= From || To - From > TimeSpan.FromHours(25) || string.IsNullOrWhiteSpace(TimeZone))
            throw new JsonException("Narrative range must be positive and at most 25 hours.");
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
            if (!zone.HasIanaId && zone.Id != "UTC") throw new JsonException("An IANA time zone is required.");
            return zone;
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { throw new JsonException("Invalid time zone.", e); }
    }
}
internal sealed record NarrativeEpisode(DateTimeOffset StartAt, DateTimeOffset EndAt, string Title, string Summary,
    Guid[] SessionIds, Guid[] LocalApplicationIds);
internal sealed record NarrativeLine(string Start, string End, string Title, string Summary, Guid[] SessionIds, Guid[] LocalApplicationIds);
internal sealed record NarrativeEvidence(object Input, Guid[] Inputs, Observation[] Previous, HashSet<Guid> Sessions, HashSet<Guid> Applications, bool Empty);

internal static class NarrativeData
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static readonly Guid EpisodeSchema = Guid.Parse("019e1bbb-ec00-7000-8000-000000000018");
    internal static readonly ThingReference Observer = new(null, BuiltInSchemas.Program, new(null, "orisumi.narrative", "deployment"));
    internal static JsonElement Fields(Observation observation) => observation.Content;
    internal static readonly SchemaDeclaration[] Schemas = [
        new(EpisodeSchema, new("一件事", "一天中的一段时间在做什么，由模型读当天的记录写出。", "episodes",
            JsonSerializer.SerializeToElement(new { title = Text("这件事的标题"), summary = Text("做了什么、得出了什么"),
                sessionIds = References("涉及的会话"), localApplicationIds = References("涉及的本机应用") })))
    ];
    private static object Text(string description) => new { type = "string", description, required = true, nullable = false, searchable = true };
    private static object References(string description) => new { type = "array", description, required = true, nullable = false,
        items = new { type = "reference", target = "thing", role = "mention", description, nullable = false } };

    internal static NarrativeEpisode? Parse(string json, NarrativeRequest request, NarrativeEvidence evidence, HashSet<Guid> used)
    {
        try
        {
            var line = JsonSerializer.Deserialize<NarrativeLine>(json, Json);
            if (line is null || string.IsNullOrWhiteSpace(line.Title) || string.IsNullOrWhiteSpace(line.Summary)
                || line.SessionIds is null || line.LocalApplicationIds is null
                || line.SessionIds.Any(id => !evidence.Sessions.Contains(id) || used.Contains(id))
                || line.LocalApplicationIds.Any(id => !evidence.Applications.Contains(id))) return null;
            var zone = request.Zone();
            var start = Instant(line.Start, request, zone, false);
            var end = Instant(line.End, request, zone, true);
            if (start is null || end is null || end <= start || start < request.From || end > request.To) return null;
            used.UnionWith(line.SessionIds);
            return new(start.Value, end.Value, line.Title, line.Summary, line.SessionIds.Distinct().ToArray(), line.LocalApplicationIds.Distinct().ToArray());
        }
        catch (Exception e) when (e is JsonException or FormatException) { return null; }
    }

    private static DateTimeOffset? Instant(string text, NarrativeRequest request, TimeZoneInfo zone, bool end)
    {
        if (!TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) return null;
        var firstDate = TimeZoneInfo.ConvertTime(request.From, zone).Date;
        var lastDate = TimeZoneInfo.ConvertTime(request.To, zone).Date;
        var candidates = new List<DateTimeOffset>();
        for (var date = firstDate; date <= lastDate; date = date.AddDays(1))
        {
            var local = DateTime.SpecifyKind(date + time.ToTimeSpan(), DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(local)) continue;
            var offsets = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local) : [zone.GetUtcOffset(local)];
            foreach (var offset in offsets)
            {
                var instant = new DateTimeOffset(local, offset);
                if (end ? instant > request.From && instant <= request.To : instant >= request.From && instant < request.To)
                    candidates.Add(instant);
            }
        }
        // A repeated wall-clock minute needs an unambiguous instant in the requested range.
        return candidates.Count == 1 ? candidates[0] : null;
    }
}
