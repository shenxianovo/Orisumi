using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orisumi.Observer.Sdk;

public sealed record RecognitionKey([property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Scope, string Scheme, string Value);
[JsonConverter(typeof(RefConverter))]
public sealed record Ref(Guid? Id = null, Guid? SchemaId = null, RecognitionKey? Key = null)
{
    public static Ref Saved(Guid id) => new(Id: id);
    public static Ref Recognize(Guid schemaId, RecognitionKey key) => new(SchemaId: schemaId, Key: key);
}
internal sealed class RefConverter : JsonConverter<Ref>
{
    public override Ref Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        return value.ValueKind == JsonValueKind.String ? Ref.Saved(value.GetGuid())
            : Ref.Recognize(value.GetProperty("schemaId").GetGuid(), value.GetProperty("key").Deserialize<RecognitionKey>(options)!);
    }
    public override void Write(Utf8JsonWriter writer, Ref value, JsonSerializerOptions options)
    {
        if (value.Id is { } id) { writer.WriteStringValue(id); return; }
        writer.WriteStartObject(); writer.WriteString("schemaId", value.SchemaId!.Value);
        writer.WritePropertyName("key"); JsonSerializer.Serialize(writer, value.Key, options); writer.WriteEndObject();
    }
}
public sealed record ThingTypeDefinition(Guid SchemaId, string DisplayName, string Description, string ResourceName)
{
    public static ThingTypeDefinition[] Load(Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource)!;
        return JsonSerializer.Deserialize<ThingTypeDefinition[]>(stream, DataClient.Json)!;
    }
}
public sealed record ObserverDescription(string Platform, string? HostName, string ProgramVersion, string? Model = null, string? DecisionModel = null)
{
    public static ObserverDescription Local(string version, string? model = null, string? decisionModel = null) =>
        new(OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux", Environment.MachineName, version, model, decisionModel);
}
public sealed record FactTime([property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? StartAt, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? EndAt, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? TimeZone)
{
    public static FactTime Point(DateTimeOffset at, string? zone) => new(at, at, zone);
    public static FactTime Unknown() => new(null, null, null);
}
public sealed record WriteItem(Guid SchemaId, object Content, string Formation, RecognitionKey? Key = null,
    FactTime? Time = null, Guid[]? Inputs = null, Ref? SpeakerId = null, Guid[]? Replaces = null, Guid? RunId = null);
public sealed record SchemaDefinition(Guid SchemaId, JsonElement Content)
{
    public static SchemaDefinition[] Load(Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException($"Missing schema definitions: {resource}");
        return JsonSerializer.Deserialize<SchemaDefinition[]>(stream, DataClient.Json)!;
    }
}
/// <summary>The running device from the Observer context, shared by every Observer that refers to it.</summary>
public sealed record DeviceContext(RecognitionKey Key, string? Name, string Platform)
{
    public static SchemaDefinition[] Definitions { get; } = SchemaDefinition.Load(typeof(DeviceContext).Assembly, "Orisumi.Observer.Sdk.device.schema.json");
    public static ThingTypeDefinition[] ThingTypes { get; } = ThingTypeDefinition.Load(typeof(DeviceContext).Assembly, "Orisumi.Observer.Sdk.device.thing-types.json");
    public Ref Ref => Ref.Recognize(ThingTypes[0].SchemaId, Key);
    public IEnumerable<WriteItem> Writes()
    {
        yield return new(Definitions[0].SchemaId, new { deviceId = Ref, platform = Platform }, "direct",
            new(Key.Scope, "orisumi.device-description", Key.Value));
        if (Name is not null) yield return new(CommonSchemas.Name, new { thingId = Ref, name = Name }, "direct",
            new(Key.Scope, "orisumi.device-name", Key.Value));
    }
}
public sealed record WriteResult(JsonElement? Observation, bool Changed, JsonElement? Error, bool Skipped, Guid[]? Replaced);
public sealed class WriteRejectedException(JsonElement error) : Exception(error.GetProperty("detail").GetString())
{
    public JsonElement Error { get; } = error;
}

public static class CommonSchemas
{
    public static readonly Guid Person = Guid.Parse("019e1bbb-ec00-7000-8000-000000000006");
    public static readonly Guid Model = Guid.Parse("019e1bbb-ec00-7000-8000-00000000001d");
    public static readonly Guid SameAs = Guid.Parse("019e1bbb-ec00-7000-8000-00000000001a");
    public static readonly Guid Adoption = Guid.Parse("019e1bbb-ec00-7000-8000-00000000001b");
    public static readonly Guid Relation = Guid.Parse("019e1bbb-ec00-7000-8000-00000000000f");
    public static readonly Guid Name = Guid.Parse("019e1bbb-ec00-7000-8000-000000000010");
    public static readonly Guid Review = Guid.Parse("019e1bbb-ec00-7000-8000-00000000000e");
    public static readonly Guid Program = Guid.Parse("019e1bbb-ec00-7000-8000-000000000012");
}
