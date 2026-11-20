using System.CommandLine;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Orisumi.CommandLine;

internal static class TimestampOption
{
    internal static Option<DateTimeOffset> Create(string name, string description) => new(name)
    {
        Description = description,
        Required = true,
        CustomParser = result =>
        {
            var value = result.Tokens.Single().Value;
            if (Regex.IsMatch(value, @"(?:Z|[+-]\d{2}:\d{2})\z") &&
                DateTimeOffset.TryParseExact(value,
                    ["yyyy-MM-dd'T'HH:mmK", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"],
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
                return timestamp;
            result.AddError($"{name} 必须是带 Z 或 UTC 偏移的 ISO 8601 时间。");
            return default;
        },
    };
}
