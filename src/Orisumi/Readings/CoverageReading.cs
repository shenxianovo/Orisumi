namespace Orisumi.Readings;

internal sealed record Cover(string Device, string Kind, DateTimeOffset Start, DateTimeOffset End, string Note, string Position);

internal static class CoverageReading
{
    public const string Name = "coverage";

    private static readonly TimeSpan Join = TimeSpan.FromMinutes(30);

    public static IEnumerable<Cover> Read(Store store, Capture heartbeat, IEnumerable<TimelineEntry> timeline) =>
        Present(timeline).Concat(Lost(store, heartbeat)).OrderBy(cover => cover.Device, StringComparer.Ordinal).ThenBy(cover => cover.Start);

    private static IEnumerable<Cover> Present(IEnumerable<TimelineEntry> timeline) =>
        timeline.Where(span => span.Kind != "提交")
            .GroupBy(span => (span.Device, span.Kind))
            .SelectMany(group => Merge(group.Select(span => (span.Start, span.End, span.Position)))
                .Select(range => new Cover(group.Key.Device, group.Key.Kind, range.Start, range.End, "有数据", range.Position)));

    private static IEnumerable<Cover> Lost(Store store, Capture capture)
    {
        var streams = TimelineReading.Streams(store, capture);
        var gaps = JsonLines.Read(store.PathOf(capture, "fact-gaps.jsonl")).Select(entry =>
        {
            var row = Json.Parse(entry.Text);
            var (device, source) = streams.GetValueOrDefault(row.Text("StreamId")!, ("?", "?"));
            return (Device: device, Source: source, Reason: row.Text("Reason") ?? "",
                Start: Clock.FromTicks(row.Child("Start").GetInt64()), End: Clock.FromTicks(row.Child("End").GetInt64()),
                Lost: row.Child("EstimatedFactsLost").TryGetInt32(out var lost) ? lost : 0,
                Position: capture.At("fact-gaps.jsonl", entry.Line).ToString());
        }).ToList();
        foreach (var group in gaps.GroupBy(gap => (gap.Device, gap.Source, gap.Reason)))
        {
            foreach (var range in Merge(group.Select(gap => (gap.Start, gap.End, gap.Position))))
            {
                var lost = group.Where(gap => gap.Start >= range.Start && gap.Start <= range.End).Sum(gap => gap.Lost);
                yield return new(group.Key.Device, $"丢失 {group.Key.Source}", range.Start, range.End,
                    $"{group.Key.Reason}，估计丢了 {lost} 条", range.Position);
            }
        }
    }

    private static IEnumerable<(DateTimeOffset Start, DateTimeOffset End, string Position)> Merge(
        IEnumerable<(DateTimeOffset Start, DateTimeOffset End, string Position)> ranges)
    {
        (DateTimeOffset Start, DateTimeOffset End, string Position)? current = null;
        foreach (var range in ranges.OrderBy(range => range.Start))
        {
            if (current is { } open && range.Start - open.End <= Join)
            {
                current = open with { End = range.End > open.End ? range.End : open.End };
                continue;
            }
            if (current is { } done) yield return done;
            current = range;
        }
        if (current is { } last) yield return last;
    }
}
