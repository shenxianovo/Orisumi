using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Orisumi.Platform;

internal static class ChromeExtension
{
    internal static string Id(string absolutePath) =>
        string.Concat(SHA256.HashData(Encoding.UTF8.GetBytes(absolutePath)).Take(16)
            .SelectMany(value => new[] { (char)('a' + (value >> 4)), (char)('a' + (value & 15)) }));

    internal static string DirectoryFor(Instance instance, string? checkout) => DirectoryPaths.Resolve(checkout is null
        ? Path.Combine(instance.Store.Collector("browser"), "extension")
        : Path.Combine(checkout, ".local", "extension"));

    internal static void Configure(string directory, Instance instance)
    {
        var manifest = Path.Combine(directory, "manifest.json");
        var content = JsonNode.Parse(File.ReadAllText(manifest)) as JsonObject ??
            throw new InvalidDataException($"无效的 Chrome 扩展清单：{manifest}");
        content["name"] = $"Orisumi Chrome Collector ({instance.Name})";
        File.WriteAllText(manifest, content.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(directory, "config.json"), FormattableString.Invariant($"{{\"instance\":\"{instance.Name}\",\"port\":{instance.BrowserPort}}}\n"));
    }

    internal static void CheckConfiguration(string directory, Instance instance)
    {
        var config = Path.Combine(directory, "config.json");
        if (!File.Exists(config)) throw new FileNotFoundException($"找不到扩展配置：{config}。请安装或构建 Chrome 扩展。");
        using var json = JsonDocument.Parse(File.ReadAllText(config));
        if (json.RootElement.ValueKind != JsonValueKind.Object ||
            !json.RootElement.TryGetProperty("instance", out var name) || name.ValueKind != JsonValueKind.String ||
            name.GetString() != instance.Name || !json.RootElement.TryGetProperty("port", out var port) ||
            port.ValueKind != JsonValueKind.Number || !port.TryGetInt32(out var number) || number != instance.BrowserPort)
            throw new InvalidDataException($"Chrome 扩展未配置给 {instance.Name}：{directory}");
    }
}
