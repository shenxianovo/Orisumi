using System.Text;
using System.Text.Json;

namespace Orisumi.Readings;

internal static class Json
{
    public static string? Text(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static JsonElement Child(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    public static bool IsTrue(this JsonElement element, string name) =>
        element.Child(name).ValueKind == JsonValueKind.True;

    public static IEnumerable<JsonElement> Items(this JsonElement element) =>
        element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : [];

    public static string Blocks(JsonElement content, string type = "text")
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString()!;
        var text = new StringBuilder();
        foreach (var block in content.Items().Where(block => block.Text("type") == type))
        {
            if (text.Length > 0) text.Append('\n');
            text.Append(block.Text("text"));
        }
        return text.ToString();
    }

    public static JsonElement Parse(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }
}
