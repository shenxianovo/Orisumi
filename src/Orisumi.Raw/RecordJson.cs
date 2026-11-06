using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orisumi.Raw;

internal static class RecordJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new BlockNameConverter(), new UtcMillisecondsConverter() },
    };

    public static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T Decode<T>(ReadOnlySpan<byte> json) => JsonSerializer.Deserialize<T>(json, Options)!;

    public static string? KindOf(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.TryGetProperty("kind", out var kind) ? kind.GetString() : null;
    }

    public static DateTimeOffset Milliseconds(DateTimeOffset at) => DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds());

    private sealed class BlockNameConverter : JsonConverter<BlockName>
    {
        public override BlockName Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            BlockName.Parse(reader.GetString()!);

        public override void Write(Utf8JsonWriter writer, BlockName value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }

    private sealed class UtcMillisecondsConverter : JsonConverter<DateTimeOffset>
    {
        private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            DateTimeOffset.ParseExact(reader.GetString()!, Format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture));
    }
}
