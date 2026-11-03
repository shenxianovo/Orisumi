using System.Text.Json;
using Orisumi.Readings;

namespace Orisumi.Context;

internal static class Rendering
{
    private const int Long = 4000;
    private const int Short = 240;

    public static string? Render(string source, string line) => source switch
    {
        "claude-code" => Claude(Json.Parse(line)),
        "codex" => Codex(Json.Parse(line)),
        _ => Cut(line, Long),
    };

    public static string Cut(string? text, int limit)
    {
        var flat = (text ?? "").Trim();
        return flat.Length <= limit ? flat : $"{flat[..limit]}…(+{flat.Length - limit})";
    }

    private static string? Claude(JsonElement record)
    {
        var at = record.Text("timestamp") is { } timestamp ? Clock.Show(Clock.Parse(timestamp)) : "";
        var content = record.Child("message").Child("content");
        switch (record.Text("type"))
        {
            case "user" when record.Child("origin").Text("kind") == "human":
                return $"{at} 人：{Cut(Json.Blocks(content), Long)}";
            case "user":
                var results = content.Items().Where(block => block.Text("type") == "tool_result").ToList();
                if (results.Count > 0)
                    return $"{at} 工具结果：{Cut(string.Join(' ', results.Select(block => Json.Blocks(block.Child("content")))), Short)}";
                return $"{at} 注入：{Cut(Json.Blocks(content), Short)}";
            case "assistant":
                var parts = content.Items().Select(block => block.Text("type") switch
                {
                    "text" => $"助手：{Cut(block.Text("text"), Long)}",
                    "tool_use" => $"调用 {block.Text("name")}：{Cut(block.Child("input").GetRawText(), Short)}",
                    _ => null,
                }).OfType<string>().ToList();
                return parts.Count == 0 ? null : $"{at} {string.Join(' ', parts)}";
            default:
                return null;
        }
    }

    private static string? Codex(JsonElement record)
    {
        if (record.Child("metadata").IsTrue("inherited_user_message")) return null;
        var at = record.Text("timestamp") is { } timestamp ? Clock.Show(Clock.Parse(timestamp)) : "";
        var payload = record.Child("payload");
        if (record.Text("type") != "response_item") return null;
        return payload.Text("type") switch
        {
            "message" when payload.Text("role") == "user" => $"{at} 用户消息：{Cut(Json.Blocks(payload.Child("content"), "input_text"), Long)}",
            "message" when payload.Text("role") == "assistant" => $"{at} 助手：{Cut(Json.Blocks(payload.Child("content"), "output_text"), Long)}",
            "function_call" or "custom_tool_call" => $"{at} 调用 {payload.Text("name")}：{Cut(payload.Text("arguments") ?? payload.Text("input"), Short)}",
            "function_call_output" or "custom_tool_call_output" => $"{at} 工具结果：{Cut(payload.Child("output").ValueKind == JsonValueKind.String ? payload.Text("output") : payload.Child("output").GetRawText(), Short)}",
            _ => null,
        };
    }
}
