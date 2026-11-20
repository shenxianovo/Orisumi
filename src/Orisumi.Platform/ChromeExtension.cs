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

    internal static void Configure(string directory, Instance instance)
    {
        var manifest = Path.Combine(directory, "manifest.json");
        var content = JsonNode.Parse(File.ReadAllText(manifest)) as JsonObject ??
            throw new InvalidDataException($"无效的 Chrome 扩展清单：{manifest}");
        content["name"] = $"Orisumi Chrome Collector ({instance.Name})";
        File.WriteAllText(manifest, content.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(directory, "config.json"), $"{{\"nativeHost\":\"{instance.ChromeHost}\"}}\n");
    }

    internal static void CheckConfiguration(string directory, Instance instance)
    {
        var config = Path.Combine(directory, "config.json");
        if (!File.Exists(config)) throw new FileNotFoundException($"找不到扩展配置：{config}。请安装或构建 Chrome 扩展。");
        using var json = JsonDocument.Parse(File.ReadAllText(config));
        if (json.RootElement.ValueKind != JsonValueKind.Object ||
            !json.RootElement.TryGetProperty("nativeHost", out var host) || host.ValueKind != JsonValueKind.String ||
            host.GetString() != instance.ChromeHost)
            throw new InvalidDataException($"Chrome 扩展未配置给 {instance.Name}：{directory}");
    }

    internal static string Register(Instance instance, string extensionId, string[] command, SystemLocations locations)
    {
        if (extensionId.Length != 32 || extensionId.Any(character => character is < 'a' or > 'p'))
            throw new ArgumentException("expected a 32-character Chrome extension ID");

        var store = instance.Store;
        using var guard = Store.WaitLock(store.LockFile("chrome-registration"));
        var launcher = store.ChromeLauncher;
        File.WriteAllText(launcher, $"#!/bin/sh\nexec {string.Join(' ', command.Select(ShellQuote))}\n");
        File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Directory.CreateDirectory(locations.ChromeManifests);
        var manifest = Manifest(instance, locations);
        using var file = File.Create(manifest);
        using var json = new Utf8JsonWriter(file);
        json.WriteStartObject();
        json.WriteString("name", instance.ChromeHost);
        json.WriteString("description", "Orisumi browser collector");
        json.WriteString("path", launcher);
        json.WriteString("type", "stdio");
        json.WriteStartArray("allowed_origins");
        json.WriteStringValue($"chrome-extension://{extensionId}/");
        json.WriteEndArray();
        json.WriteEndObject();
        return manifest;
    }

    internal static string Manifest(Instance instance, SystemLocations locations) =>
        Path.Combine(locations.ChromeManifests, instance.ChromeHost + ".json");

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
}
