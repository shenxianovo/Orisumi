using System.Text.Json;
using System.Text.RegularExpressions;

namespace Orisumi.Cli.Day;

internal sealed class DayTitles(List<DayTitles.Rule> rules)
{
    internal sealed record Rule(string Field, string? AppId, Regex Pattern, string Replacement, int Index);

    internal static DayTitles Read(string path)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return new([]); }

        var reader = new Utf8JsonReader(bytes.AsSpan().StartsWith("\uFEFF"u8) ? bytes.AsSpan(3) : bytes);
        var index = 0;
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new JsonException();
            var rules = new List<Rule>();
            var found = false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isRules = reader.ValueTextEquals("rules"u8);
                if (!reader.Read()) throw new JsonException();
                if (!isRules)
                {
                    reader.Skip();
                    continue;
                }
                if (found || reader.TokenType != JsonTokenType.StartArray) throw new JsonException();
                found = true;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    using var entry = JsonDocument.ParseValue(ref reader);
                    var field = Required(entry.RootElement, "field");
                    if (field is not ("window_title" or "tab_title"))
                        throw new JsonException($"未知的 field：{field}");
                    string? appId = null;
                    if (entry.RootElement.TryGetProperty("app_id", out var identifier))
                    {
                        if (field != "window_title" || identifier.ValueKind != JsonValueKind.String)
                            throw new JsonException("app_id 只能用于 window_title，且必须是字符串。");
                        appId = identifier.GetString();
                    }
                    var pattern = new Regex(Required(entry.RootElement, "pattern"), RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(100));
                    rules.Add(new(field, appId, pattern, Required(entry.RootElement, "replacement"), index++));
                }
                if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException();
            }
            if (!found || reader.TokenType != JsonTokenType.EndObject || reader.Read()) throw new JsonException();
            return new(rules);
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            throw new InvalidDataException($"titles.json 规则索引 {index}：{error.Message}", error);
        }

        static string Required(JsonElement rule, string key) =>
            rule.ValueKind == JsonValueKind.Object && rule.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()! : throw new JsonException($"{key} 必须是字符串。");
    }

    internal string? Apply(string field, string? title, string? appId = null)
    {
        if (title is null) return null;
        foreach (var rule in rules)
        {
            if (rule.Field != field || rule.AppId is not null && rule.AppId != appId) continue;
            try { title = rule.Pattern.Replace(title, rule.Replacement); }
            catch (RegexMatchTimeoutException error)
            {
                throw new InvalidDataException($"titles.json 规则索引 {rule.Index}：标题匹配超时。", error);
            }
        }
        return title;
    }
}
