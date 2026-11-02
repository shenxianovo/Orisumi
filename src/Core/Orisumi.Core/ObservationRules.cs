using System.Text.Json;

namespace Orisumi.Core;

public static class ObservationRules
{
    public static bool IsUuid7(Guid id) => id.Version == 7 && (id.ToByteArray()[8] & 0xc0) == 0x80;
    public static bool ValidFormation(string formation) => formation is "direct" or "stated" or "computed" or "extracted" or "inferred";
    public static bool IsModel(Observation observation) => observation.Formation is "inferred" or "extracted";

    public static void ValidateSchema(Schema schema)
    {
        ValidateType(schema.DisplayName, schema.Description, schema.ResourceName);
        if (SchemaRules.FieldViolation(schema.Fields) is { } fieldRule) throw new ModelViolationException(fieldRule, "Invalid field definitions.");
        foreach (var (fields, rule) in new[] { (schema.StateFields, "SCHEMA-20"), (schema.ClaimFields, "SCHEMA-21") })
            Require(fields is null || fields.All(field => schema.Fields.TryGetProperty(field, out _)), rule, "Keys must name top-level fields.");
    }

    public static void ValidateType(string displayName, string description, string resourceName)
    {
        Require(!string.IsNullOrWhiteSpace(displayName) && !string.IsNullOrWhiteSpace(description), "SCHEMA-4", "Type name and description must be nonempty.");
        Require(SchemaRules.ValidResource(resourceName), "SCHEMA-6", "Invalid resourceName.");
    }

    public static void Validate(Observation observation, Schema schema)
    {
        Require(ValidFormation(observation.Formation), "OBSERVATION-11", "Invalid formation.");
        Require(observation.StartAt is null || observation.EndAt is null || observation.StartAt <= observation.EndAt,
            "OBSERVATION-6", "startAt must not be later than endAt.");
        Require(observation.TimeZone is null || TimeZoneInfo.TryConvertIanaIdToWindowsId(observation.TimeZone, out _),
            "OBSERVATION-9", "timeZone must be an IANA time zone or null.");
        if (SchemaRules.ContentViolation(schema.Fields, observation.Content) is { } rule)
            throw new ModelViolationException(rule, "Content does not match its SchemaRevision.");
    }

    public static IReadOnlyList<ObservationReference> GetReferences(Observation observation, Schema schema, InputSet? inputs = null)
    {
        var result = new List<ObservationReference> { new("/observerId", observation.ObserverId, "thing") };
        if (observation.SpeakerId is { } speaker) result.Add(new("/speakerId", speaker, "thing"));
        if (observation.ObserverDescriptionId is { } description) result.Add(new("/observerDescriptionId", description, "observation"));
        for (var i = 0; i < observation.Replaces.Length; i++) result.Add(new($"/replaces/{i}", observation.Replaces[i], "observation"));
        if (inputs is not null)
            foreach (var id in inputs.Members) result.Add(new("/inputSetId", id, "observation"));
        Walk(schema.Fields, observation.Content, "/content", (definition, value, path) =>
        {
            if (definition.GetProperty("type").GetString() == "reference")
                result.Add(new(path, value.GetGuid(), definition.GetProperty("target").GetString()!,
                    definition.TryGetProperty("role", out var role) ? role.GetString() : null));
        });
        return result;
    }

    public static string? SearchText(Schema schema, JsonElement content)
    {
        var values = new List<string>();
        Walk(schema.Fields, content, "/content", (definition, value, _) =>
        {
            if (definition.TryGetProperty("searchable", out var flag) && flag.GetBoolean()) values.Add(value.GetString()!);
        });
        return values.Count == 0 ? null : string.Join('\n', values);
    }

    public static void Walk(JsonElement fields, JsonElement content, string path, Action<JsonElement, JsonElement, string> visit)
    {
        foreach (var field in fields.EnumerateObject())
            if (content.TryGetProperty(field.Name, out var value))
                WalkValue(field.Value, value, path + "/" + field.Name.Replace("~", "~0").Replace("/", "~1"), visit);
    }

    private static void WalkValue(JsonElement definition, JsonElement value, string path, Action<JsonElement, JsonElement, string> visit)
    {
        if (value.ValueKind == JsonValueKind.Null) return;
        switch (definition.GetProperty("type").GetString())
        {
            case "object": Walk(definition.GetProperty("fields"), value, path, visit); break;
            case "array":
                var index = 0;
                foreach (var item in value.EnumerateArray()) WalkValue(definition.GetProperty("items"), item, $"{path}/{index++}", visit);
                break;
            default: visit(definition, value, path); break;
        }
    }

    public static void Require(bool condition, string rule, string message, bool conflict = false)
    {
        if (!condition) throw new ModelViolationException(rule, message, conflict);
    }
}
