namespace Orisumi.Cli.Day;

internal sealed record DaySpan<T>(long Start, long End, T Value);
internal readonly record struct DayPosition(long Run, long Sequence, long At);
internal readonly record struct DayChange<T>(DayPosition Position, T Value);

internal static class DayTimeline
{
    internal static IEnumerable<DaySpan<T>> Values<T>(DayRange day, IReadOnlyDictionary<long, DaySpan<long>> runs,
        IEnumerable<DayChange<T>> changes, Func<DayPosition, long?>? channelEnd = null)
    {
        foreach (var group in changes.GroupBy(change => change.Position.Run))
        {
            if (!runs.TryGetValue(group.Key, out var run)) continue;
            var rows = group.OrderBy(change => change.Position.Sequence).ToArray();
            for (var i = 0; i < rows.Length; i++)
            {
                var (position, value) = rows[i];
                var limit = channelEnd is null ? long.MaxValue : channelEnd(position);
                if (limit is null) continue;
                var start = Math.Max(day.From, Math.Max(run.Start, position.At));
                var next = i + 1 < rows.Length ? rows[i + 1].Position.At : long.MaxValue;
                var end = Math.Min(day.To, Math.Min(run.End, Math.Min(next, limit.Value)));
                if (start < end) yield return new(start, end, value);
            }
        }
    }

    internal static List<DaySpan<T>> Overlay<T>(IEnumerable<DaySpan<T>> spans, Func<DaySpan<T>, long>? rank = null)
    {
        var ordered = spans.OrderBy(span => span.Start).ToArray();
        var active = new PriorityQueue<DaySpan<T>, (long Rank, int Index)>();
        var result = new List<DaySpan<T>>();
        var i = 0;
        var at = long.MinValue;
        while (i < ordered.Length || active.Count > 0)
        {
            if (active.Count == 0) at = ordered[i].Start;
            while (i < ordered.Length && ordered[i].Start == at)
            {
                var span = ordered[i];
                active.Enqueue(span, (-(rank?.Invoke(span) ?? span.Start), -i));
                i++;
            }
            while (active.TryPeek(out var expired, out _) && expired.End <= at) active.Dequeue();
            if (!active.TryPeek(out var current, out _)) continue;
            var end = i < ordered.Length ? Math.Min(current.End, ordered[i].Start) : current.End;
            Append(result, new(at, end, current.Value));
            at = end;
        }
        return result;
    }

    internal static IEnumerable<(long Start, long End, DaySpan<T>? Left, DaySpan<U>? Right)> Align<T, U>(
        IReadOnlyList<DaySpan<T>> left, IReadOnlyList<DaySpan<U>> right)
    {
        var i = 0;
        var j = 0;
        var at = Math.Min(Start(left, i), Start(right, j));
        while (at != long.MaxValue)
        {
            if (i < left.Count && left[i].End == at) i++;
            if (j < right.Count && right[j].End == at) j++;
            var a = Start(left, i) <= at ? left[i] : null;
            var b = Start(right, j) <= at ? right[j] : null;
            var end = Math.Min(a?.End ?? Start(left, i), b?.End ?? Start(right, j));
            if (a is not null || b is not null) yield return (at, end, a, b);
            at = end;
        }

        static long Start<V>(IReadOnlyList<DaySpan<V>> spans, int index) =>
            index < spans.Count ? spans[index].Start : long.MaxValue;
    }

    internal static void Append<T>(List<DaySpan<T>> spans, DaySpan<T> span)
    {
        if (spans.Count > 0 && spans[^1].End == span.Start && EqualityComparer<T>.Default.Equals(spans[^1].Value, span.Value))
            spans[^1] = spans[^1] with { End = span.End };
        else
            spans.Add(span);
    }
}
