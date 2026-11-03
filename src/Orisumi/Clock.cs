using System.CommandLine;
using System.Globalization;

namespace Orisumi;

internal static class Clock
{
    public static TimeZoneInfo Zone { get; } = TimeZoneInfo.Local;

    public static DateTimeOffset ParseLocal(string text)
    {
        var local = DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.None);
        return new DateTimeOffset(local, Zone.GetUtcOffset(local));
    }

    public static DateTimeOffset Parse(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);

    public static DateTimeOffset FromTicks(long ticks) => new(ticks, TimeSpan.Zero);

    public static long Ticks(DateTimeOffset at) => at.UtcTicks;

    public static string Show(DateTimeOffset at) =>
        TimeZoneInfo.ConvertTime(at, Zone).ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string ShowMinute(DateTimeOffset at) =>
        TimeZoneInfo.ConvertTime(at, Zone).ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string Iso(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    public static string ZoneName =>
        $"UTC{(Zone.BaseUtcOffset < TimeSpan.Zero ? "-" : "+")}{Zone.BaseUtcOffset.ToString(@"hh\:mm", CultureInfo.InvariantCulture)}";
}

internal sealed record TimeRange(DateTimeOffset From, DateTimeOffset To)
{
    public bool Contains(DateTimeOffset at) => at >= From && at < To;

    public bool Overlaps(DateTimeOffset start, DateTimeOffset end) => start < To && end > From;

    public static readonly Option<string> FromOption = new("--from")
    {
        Description = "Start in local time, e.g. 2026-10-30 or 2026-10-30T18:00",
        Required = true,
    };

    public static readonly Option<string> ToOption = new("--to")
    {
        Description = "End in local time, exclusive",
        Required = true,
    };

    public static TimeRange Of(ParseResult parse) =>
        new(Clock.ParseLocal(parse.GetRequiredValue(FromOption)), Clock.ParseLocal(parse.GetRequiredValue(ToOption)));
}
