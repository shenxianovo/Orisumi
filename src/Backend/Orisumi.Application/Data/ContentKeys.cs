using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Orisumi.Core;

namespace Orisumi.Application.Data;

public static class ContentKeys
{
    public static string Recognition(Guid schema, RecognitionKey key) => Digest(JsonSerializer.Serialize(new { schema, key.Scope, key.Scheme, key.Value }));
    public static string? Fields(Guid schema, string[]? fields, JsonElement content, bool unordered = false)
    {
        if (fields is null) return null;
        var values = fields.Select(field => content.TryGetProperty(field, out var value) ? Canonical(value) : "missing").ToArray();
        if (unordered) Array.Sort(values, StringComparer.Ordinal);
        return Digest(schema.ToString("D") + JsonSerializer.Serialize(values));
    }
    private static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string Canonical(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(',', value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).Select(property => JsonSerializer.Serialize(property.Name) + ":" + Canonical(property.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(',', value.EnumerateArray().Select(Canonical)) + "]",
        JsonValueKind.Number => Number(value.GetRawText()),
        _ => value.GetRawText(),
    };
    private static string Number(string raw)
    {
        var at = raw.IndexOfAny(['e', 'E']);
        var mantissa = at < 0 ? raw : raw[..at];
        var exponent = at < 0 ? BigInteger.Zero : BigInteger.Parse(raw[(at + 1)..], CultureInfo.InvariantCulture);
        var point = mantissa.IndexOf('.');
        if (point >= 0) exponent -= mantissa.Length - point - 1;
        var negative = mantissa.StartsWith('-');
        var digits = mantissa.Replace(".", "").TrimStart('-', '0');
        if (digits.Length == 0) return "0";
        var trimmed = digits.TrimEnd('0'); exponent += digits.Length - trimmed.Length;
        return (negative ? "-" : "") + trimmed + "e" + exponent.ToString(CultureInfo.InvariantCulture);
    }
}
