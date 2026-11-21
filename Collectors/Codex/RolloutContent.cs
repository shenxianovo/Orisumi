using System.Text;
using System.Text.Json;

namespace Orisumi.Collectors.Codex;

public static partial class CodexCollector
{
    private static string? ItemType(JsonElement item) => String(item, "type") switch
    {
        "web_search_call" => "WebSearch",
        "Extension" => String(item, "kind") switch
        {
            "web.search" => "WebSearch",
            "clock.sleep" => "Sleep",
            "image_gen.generation" => "ImageGeneration",
            _ => "Extension",
        },
        var type => type,
    };

    private static string? EventContent(string kind, JsonElement payload, JsonElement item, string? itemType)
    {
        var fields = kind == "turn_context" ? ["model", "cwd", "approval_policy", "sandbox_policy"]
            : itemType switch
            {
                "UserMessage" or "AgentMessage" => ["content"],
                "CommandExecution" => ["command", "cwd", "exit_code", "duration"],
                "FileChange" => [],
                "McpToolCall" => ["server", "tool", "arguments", "status"],
                "WebSearch" or "Sleep" => [],
                "ImageGeneration" => ["prompt", "status"],
                _ => (string[]?)null,
            };
        if (fields is null) return null;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            Copy(writer, kind == "turn_context" ? payload : item, fields);
            if (itemType == "WebSearch")
            {
                var action = Field(item, "action");
                var query = String(item, "query") ?? String(action, "query");
                if (query is not null) writer.WriteString("query", query);
                Copy(writer, action, ["queries"]);
                var status = String(item, "status") ?? (kind == "item_completed" ? "completed" : null);
                if (status is not null) writer.WriteString("status", status);
            }
            if (itemType == "Sleep" && Integer(item, "durationMs") is long duration)
                writer.WriteNumber("duration_ms", duration);
            if (itemType == "ImageGeneration" && String(item, "revisedPrompt") is { } revisedPrompt)
                writer.WriteString("revised_prompt", revisedPrompt);
            if (itemType == "CommandExecution" && String(item, "aggregated_output") is { } output)
            {
                writer.WriteNumber("output_length", output.Length);
                if (Integer(item, "exit_code") is long code && code != 0)
                    writer.WriteString("aggregated_output", output);
            }
            if (itemType == "FileChange" && Field(item, "changes") is { ValueKind: JsonValueKind.Object } changes)
            {
                writer.WriteStartObject("changes");
                foreach (var change in changes.EnumerateObject())
                {
                    writer.WriteStartObject(change.Name);
                    Copy(writer, change.Value, ["type", "move_path"]);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        return stream.Length == 2 ? null : Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    private static void Copy(Utf8JsonWriter writer, JsonElement source, string[] fields)
    {
        foreach (var name in fields)
        {
            var field = Field(source, name);
            if (field.ValueKind == JsonValueKind.Undefined) continue;
            writer.WritePropertyName(name);
            field.WriteTo(writer);
        }
    }
}
