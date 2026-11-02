using System.Text.Json;
using Orisumi.Core;

namespace Orisumi.Application.Data;

// UNDERSTANDING-4: denial, replacement, adoption; state selection follows query filtering.
public sealed class CurrentView
{
    private readonly Dictionary<Guid, ObservationIndex> _records;
    private readonly Observation[] _reviews;
    private readonly Observation[] _adoptions;
    private readonly Observation[] _sameAs;
    private readonly Dictionary<Guid, IReadOnlyList<DeniedRange>?> _denials = [];
    private readonly HashSet<Guid> _confirmed = [];
    private readonly HashSet<Guid> _replaced = [];

    public CurrentView(IReadOnlyList<ObservationIndex> records, IReadOnlyList<Observation> controls)
    {
        _records = records.ToDictionary(record => record.ObservationId);
        _reviews = controls.Where(record => _records[record.ObservationId].SchemaId == BuiltInSchemas.Review)
            .OrderByDescending(record => record.RecordedAt).ThenByDescending(record => record.ObservationId).ToArray();
        _sameAs = controls.Where(record => _records[record.ObservationId].SchemaId == BuiltInSchemas.SameAs).ToArray();
        _adoptions = controls.Where(record => _records[record.ObservationId].SchemaId == BuiltInSchemas.Adoption)
            .OrderByDescending(record => record.RecordedAt).ThenByDescending(record => record.ObservationId).ToArray();
        // Review targets precede the review, so later withdrawals are resolved first.
        foreach (var review in _reviews) Denial(_records[review.ObservationId]);
        _confirmed = _reviews.Where(review => review.Formation == "stated" && review.Content.GetProperty("verdict").GetString() == "confirm"
            && Denial(_records[review.ObservationId]) is not null && review.Content.GetProperty("targetId").ValueKind == JsonValueKind.String)
            .Select(review => review.Content.GetProperty("targetId").GetGuid()).ToHashSet();
        foreach (var replacer in records)
            foreach (var id in replacer.Replaces)
                if (_records.TryGetValue(id, out var target) && !(IsModel(replacer) && (target.Formation == "stated" || _confirmed.Contains(id)))) _replaced.Add(id);
    }

    // null means wholly denied; [] means no denied time; otherwise the union of clipped intervals.
    public IReadOnlyList<DeniedRange>? Denial(ObservationIndex record)
    {
        if (_denials.TryGetValue(record.ObservationId, out var cached)) return cached;
        var ranges = new List<DeniedRange>();
        foreach (var review in _reviews)
        {
            if (review.ObservationId == record.ObservationId || review.Content.GetProperty("verdict").GetString() != "deny") continue;
            var target = review.Content.GetProperty("targetId");
            var matches = target.ValueKind == JsonValueKind.String
                ? target.GetGuid() == record.ObservationId || _records.TryGetValue(target.GetGuid(), out var source)
                    && source.ClaimKey is not null && source.ClaimKey == record.ClaimKey
                : InScope(review.Content.GetProperty("scope"), record);
            if (!matches || target.ValueKind == JsonValueKind.String && record.Formation == "stated" && record.RecordedAt > review.RecordedAt) continue;
            var withdrawn = Denial(_records[review.ObservationId]);
            if (withdrawn is null) continue;
            if (review.StartAt is null && review.EndAt is null) return _denials[record.ObservationId] = null;
            if ((record.StartAt ?? record.EndAt) is not { } start) continue;
            var end = record.EndAt ?? start;
            var deniedStart = review.StartAt ?? review.EndAt!.Value;
            var deniedEnd = review.EndAt ?? deniedStart;
            if (!Intersects(start, end, deniedStart, deniedEnd)) continue;
            ranges.AddRange(Subtract([new(start > deniedStart ? start : deniedStart, end < deniedEnd ? end : deniedEnd)], withdrawn));
        }
        var merged = Merge(ranges);
        if (merged.Count == 1 && merged[0].StartAt == (record.StartAt ?? record.EndAt) && merged[0].EndAt == (record.EndAt ?? record.StartAt))
            return _denials[record.ObservationId] = null;
        return _denials[record.ObservationId] = merged;
    }

    private bool InScope(JsonElement scope, ObservationIndex record)
    {
        if (record.SchemaId == BuiltInSchemas.Review) return false;
        var types = scope.GetProperty("schemaIds").EnumerateArray().Select(value => value.GetGuid()).ToArray();
        if (types.Length > 0 && !types.Contains(record.SchemaId)) return false;
        var things = scope.GetProperty("thingIds").EnumerateArray().Select(value => value.GetGuid()).ToArray();
        return things.Length == 0 || record.References.Any(reference => reference.Role is not null
            && things.Any(thing => thing == reference.TargetId || record.SchemaId != BuiltInSchemas.SameAs && SameThings(thing, record).Contains(reference.TargetId)));
    }

    public bool Visible(ObservationIndex record)
    {
        if (Denial(record) is null || _replaced.Contains(record.ObservationId)) return false;
        if (!IsModel(record) || _confirmed.Contains(record.ObservationId)) return true;
        var adoption = _adoptions.FirstOrDefault(adoption => adoption.Content.GetProperty("schemaId").GetGuid() == record.SchemaId
            && Denial(_records[adoption.ObservationId]) is not null && !_replaced.Contains(adoption.ObservationId)
            && Contains(adoption, record.StartAt ?? record.EndAt)
            && !Denial(_records[adoption.ObservationId])!.Any(range => (record.StartAt ?? record.EndAt) is { } point && Intersects(point, point, range.StartAt, range.EndAt)));
        return adoption is null || adoption.Content.GetProperty("observerDescriptionId").GetGuid() == record.ObserverDescriptionId;
    }

    public IReadOnlyList<ObservationIndex> Select(IEnumerable<ObservationIndex> candidates, bool current)
    {
        if (!current) return candidates.ToArray();
        var visible = candidates.Where(Visible).ToArray();
        return visible.Where(record => record.StateKey is null).Concat(visible.Where(record => record.StateKey is not null)
            .GroupBy(record => record.StateKey).Select(group => group.OrderByDescending(record => record.Formation == "stated")
                .ThenByDescending(record => record.RecordedAt).ThenByDescending(record => record.ObservationId).First())).ToArray();
    }

    public HashSet<Guid> SameThings(Guid thing, ObservationIndex? at = null)
    {
        var found = new HashSet<Guid> { thing };
        var start = at?.StartAt ?? at?.EndAt ?? DateTimeOffset.MinValue;
        var end = at?.EndAt ?? at?.StartAt ?? DateTimeOffset.MaxValue;
        var pending = new Queue<(Guid Thing, DeniedRange Range)>(); pending.Enqueue((thing, new(start, end)));
        var visited = new HashSet<(Guid, DateTimeOffset, DateTimeOffset)>();
        while (pending.TryDequeue(out var step))
        {
            if (!visited.Add((step.Thing, step.Range.StartAt, step.Range.EndAt))) continue;
            foreach (var same in _sameAs)
            {
                var left = same.Content.GetProperty("leftId").GetGuid(); var right = same.Content.GetProperty("rightId").GetGuid();
                if (left != step.Thing && right != step.Thing || !Visible(_records[same.ObservationId])) continue;
                if (at is { StartAt: null, EndAt: null } && (same.StartAt is not null || same.EndAt is not null)) continue;
                var from = same.StartAt ?? same.EndAt ?? DateTimeOffset.MinValue;
                var to = same.EndAt ?? same.StartAt ?? DateTimeOffset.MaxValue;
                if (!Intersects(step.Range.StartAt, step.Range.EndAt, from, to)) continue;
                var intersection = new DeniedRange(step.Range.StartAt > from ? step.Range.StartAt : from, step.Range.EndAt < to ? step.Range.EndAt : to);
                foreach (var range in Subtract([intersection], Denial(_records[same.ObservationId])!))
                {
                    var other = left == step.Thing ? right : left;
                    found.Add(other); pending.Enqueue((other, range));
                }
            }
        }
        return found;
    }

    private static IReadOnlyList<DeniedRange> Subtract(IReadOnlyList<DeniedRange> ranges, IReadOnlyList<DeniedRange> exclusions)
    {
        foreach (var exclusion in exclusions)
        {
            var remaining = new List<DeniedRange>();
            foreach (var range in ranges)
            {
                if (!Intersects(range.StartAt, range.EndAt, exclusion.StartAt, exclusion.EndAt)) { remaining.Add(range); continue; }
                if (range.StartAt < exclusion.StartAt) remaining.Add(new(range.StartAt, exclusion.StartAt));
                if (range.EndAt > exclusion.EndAt) remaining.Add(new(exclusion.EndAt, range.EndAt));
            }
            ranges = remaining;
        }
        return ranges;
    }

    public Observation Decorate(Observation observation, InputSet? input)
    {
        var ranges = Denial(_records[observation.ObservationId]);
        return observation with { DeniedRanges = ranges is { Count: > 0 } ? ranges : null,
            BasisDenied = input is not null && input.Members.Any(id => _records.TryGetValue(id, out var record) && Denial(record) is not { Count: 0 }) ? true : null };
    }

    public static bool Intersects(DateTimeOffset start, DateTimeOffset end, DateTimeOffset from, DateTimeOffset to) =>
        start == end ? from == to ? start == from : from <= start && start < to
        : from == to ? start <= from && from < end : start < to && from < end;
    private static bool Contains(Observation range, DateTimeOffset? point) => range.StartAt is null && range.EndAt is null
        || point is { } time && Intersects(time, time, range.StartAt ?? range.EndAt!.Value, range.EndAt ?? range.StartAt!.Value);
    private static bool IsModel(ObservationIndex record) => record.Formation is "inferred" or "extracted";
    private static List<DeniedRange> Merge(List<DeniedRange> ranges)
    {
        var merged = new List<DeniedRange>();
        foreach (var range in ranges.OrderBy(range => range.StartAt).ThenBy(range => range.EndAt))
        {
            if (merged.Count == 0 || merged[^1].EndAt < range.StartAt) merged.Add(range);
            else if (range.EndAt > merged[^1].EndAt) merged[^1] = merged[^1] with { EndAt = range.EndAt };
        }
        return merged;
    }
}
