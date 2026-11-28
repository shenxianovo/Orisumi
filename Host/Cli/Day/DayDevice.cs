namespace Orisumi.Cli.Day;

internal sealed record DayAttention(DayApp App, string? Title, DayTab? Tab);
internal sealed record DayAttentionSpan(long Start, long End, DayAttention Value, long Active);
internal sealed record DayAppTotal(DayApp App, long Foreground, long Active);
internal sealed record DayTitleTotal(DayApp App, string? Title, long Foreground, long Active);
internal sealed record DayDatabaseInfo(string Source, string Id, string[] Missing);

internal sealed class DayDevice(string key, bool thisHost, string platform)
{
    internal bool IsChrome(DayApp app) => (platform, app.Id) is ("windows", "chrome.exe") or ("macos", "com.google.Chrome");

    internal string Key { get; } = key;
    internal bool ThisHost { get; } = thisHost;
    internal string? Name { get; set; }
    internal long NameAt { get; set; } = long.MinValue;
    internal SortedSet<string> TimeZones { get; } = new(StringComparer.Ordinal);
    internal List<DayDatabaseInfo> Databases { get; } = [];
    internal List<DaySpan<string>> Presence { get; } = [];
    internal List<DaySpan<DayWindow>> Windows { get; } = [];
    internal List<DaySpan<DayBrowserFocus>> Tabs { get; } = [];
    internal DayAgents Agents { get; } = new();
    internal List<DayVisit> Visits { get; } = [];
    internal List<DayDownload> Downloads { get; } = [];

    internal List<DaySpan<string>> PresenceFor(DayRange day) => DayTimeline.Overlay(
        day.From < day.To ? Presence.Append(new(day.From, day.To, "unobserved")) : Presence,
        span => span.Value switch
        {
            "asleep" => 5,
            "session_inactive" => 4,
            "locked" => 3,
            "active" or "idle" => 2,
            "unknown" => 1,
            _ => 0,
        });

    internal List<DayAttentionSpan> AttentionFor(List<DaySpan<string>> presence)
    {
        var windows = DayTimeline.Overlay(Windows);
        var tabs = DayTimeline.Overlay(Tabs, span => span.Value.FocusedAt);
        var contexts = new List<DaySpan<DayAttention>>();
        foreach (var (start, end, app, tab) in DayTimeline.Align(windows, tabs))
        {
            if (app is null) continue;
            var (identity, title) = app.Value;
            DayTimeline.Append(contexts, new(start, end, new(identity, title,
                IsChrome(identity) ? tab?.Value.Tab : null)));
        }

        var result = new List<DayAttentionSpan>();
        foreach (var (start, end, state, context) in DayTimeline.Align(presence, contexts))
        {
            if (state is null || state.Value is "unobserved" or "asleep") continue;
            var value = context?.Value ?? new(new(null, null), null, null);
            var active = state.Value == "active" ? end - start : 0;
            if (result.Count > 0 && result[^1].End == start && result[^1].Value == value)
                result[^1] = result[^1] with { End = end, Active = result[^1].Active + active };
            else
                result.Add(new(start, end, value, active));
        }
        return result;
    }

    internal static IEnumerable<DayAppTotal> AppTotals(IEnumerable<DayAttentionSpan> attention) =>
        attention.Where(span => span.Value.App is not { Name: null, Id: null }).GroupBy(span => span.Value.App)
            .Select(group => new DayAppTotal(group.Key, group.Sum(span => span.End - span.Start), group.Sum(span => span.Active)))
            .OrderByDescending(app => app.Active).ThenByDescending(app => app.Foreground);

    // The detailed attention list runs to hundreds of segments a day; this is the readable digest.
    internal static IEnumerable<DayTitleTotal> TitleTotals(IEnumerable<DayAttentionSpan> attention, long minimum) =>
        attention.Where(span => span.Value.App is not { Name: null, Id: null })
            .GroupBy(span => (span.Value.App, span.Value.Title))
            .Select(group => new DayTitleTotal(group.Key.App, group.Key.Title,
                group.Sum(span => span.End - span.Start), group.Sum(span => span.Active)))
            .Where(title => title.Foreground >= minimum)
            .OrderByDescending(title => title.Active).ThenByDescending(title => title.Foreground);
}
