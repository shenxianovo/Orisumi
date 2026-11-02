using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Orisumi.Core;

public static partial class ObservationJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web) { Converters = { new ObservationTimestampConverter() } };
    public static JsonElement Saved(Observation observation) => JsonSerializer.SerializeToElement(observation, Options);
    public static Observation ReadSaved(JsonElement value) => value.Deserialize<Observation>(Options)!;
    public static Schema ReadSchema(JsonElement value) => value.Deserialize<Schema>(Options)!;
    public static JsonElement Content(Schema schema) => JsonSerializer.SerializeToElement(schema, Options);
    public static DateTimeOffset ParseTime(string value)
    {
        if (!TimestampPattern().IsMatch(value)) throw new JsonException("Time must include an explicit UTC offset.");
        // Parse only whole microseconds: DateTimeOffset otherwise rounds longer fractions before truncation.
        var dot = value.IndexOf('.');
        if (dot >= 0)
        {
            var end = dot + 1;
            while (end < value.Length && char.IsAsciiDigit(value[end])) end++;
            if (end - dot - 1 > 6) value = value[..(dot + 7)] + value[end..];
        }
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            throw new JsonException("Invalid timestamp.");
        return ObservationTime.Normalize(time);
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[Tt]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:[Zz]|[+-]\d{2}:\d{2})\z")]
    private static partial Regex TimestampPattern();

}

public sealed class ObservationTimestampConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => ObservationJson.ParseTime(reader.GetString()!);
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) => writer.WriteStringValue(ObservationTime.Format(value));
}
