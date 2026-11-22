using System.Globalization;

namespace Orisumi.Cli.Day;

internal sealed record DayRange(DateOnly Date, TimeZoneInfo Zone, long From, long To)
{
    internal static DayRange Create(string? date, string? zoneId)
    {
        TimeZoneInfo zone;
        try { zone = zoneId is null ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(zoneId); }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ArgumentException($"无法读取 IANA 时区：{zoneId}", error);
        }
        if (zoneId is not null && !zone.HasIanaId)
            throw new ArgumentException($"--time-zone 需要 IANA 时区：{zoneId}");

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
        var day = today;
        if (date is not null && !DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
            throw new ArgumentException("--date 需要 YYYY-MM-DD 格式。");
        // Today ends now: the rest of the day hasn't happened, so it isn't unobserved.
        var from = Midnight(day, zone);
        return new(day, zone, from, Math.Max(from, Math.Min(Midnight(day.AddDays(1), zone), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())));
    }

    internal string Format(long milliseconds) =>
        TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds), Zone)
            .ToString("yyyy-MM-dd'T'HH:mm:ss.FFFzzz", CultureInfo.InvariantCulture);

    private static long Midnight(DateOnly date, TimeZoneInfo zone)
    {
        var time = date.ToDateTime(TimeOnly.MinValue);
        if (zone.IsInvalidTime(time))
        {
            // A forward transition can skip midnight or the entire date.
            var valid = time.AddMinutes(1);
            while (zone.IsInvalidTime(valid)) valid = valid.AddMinutes(1);
            const long ticksPerMs = TimeSpan.TicksPerMillisecond;
            var low = valid.AddMinutes(-1).Ticks / ticksPerMs;
            var high = valid.Ticks / ticksPerMs;
            while (high - low > 1)
            {
                var mid = (low + high) / 2;
                if (zone.IsInvalidTime(new DateTime(mid * ticksPerMs))) low = mid;
                else high = mid;
            }
            time = new DateTime(high * ticksPerMs);
        }
        var offset = zone.IsAmbiguousTime(time) ? zone.GetAmbiguousTimeOffsets(time).Max() : zone.GetUtcOffset(time);
        return new DateTimeOffset(time, offset).ToUnixTimeMilliseconds();
    }
}
