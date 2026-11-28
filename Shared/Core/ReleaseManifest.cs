using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orisumi.Core;

internal sealed record ReleaseManifest(
    [property: JsonRequired] string? Version,
    [property: JsonRequired] string Instance,
    [property: JsonRequired] string Platform,
    [property: JsonRequired] ReleaseSource Source,
    [property: JsonRequired] Dictionary<string, ReleaseComponent> Components)
{
    internal const string FileName = "manifest.json";

    public Dictionary<string, SchemaTable[]> Schema { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("installed_at_utc_ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? InstalledAtUtcMs { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReleasePackage? Package { get; init; }

    internal static string ComponentDirectory(string release, string name) =>
        name == "host" ? Path.Combine(release, "host") : Path.Combine(release, "collectors", name);

    internal static ReleaseManifest Read(string path) =>
        JsonSerializer.Deserialize(File.ReadAllText(path), ReleaseJson.Default.ReleaseManifest) ??
        throw new InvalidDataException($"无效的发布清单：{path}");

    internal static ReleaseManifest? ReadInstalled(Store store)
    {
        try { return Read(store.Manifest); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    internal void Write(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(this, ReleaseJson.Default.ReleaseManifest) + "\n");

    internal bool HasSameComponents(ReleaseManifest? other) =>
        other is not null && Platform == other.Platform && Components.Count == other.Components.Count &&
        Components.All(item => other.Components.GetValueOrDefault(item.Key)?.Identity == item.Value.Identity);

    internal void VerifyFor(Instance instance, string rid)
    {
        if (Platform != rid) throw new InvalidDataException($"不支持的发布平台：{Platform}");
        if (Instance != instance.Name) throw new InvalidDataException($"发布实例为 {Instance}，目标实例为 {instance.Name}。");
        if (Version is { Length: 0 } || Source is null || !IsTreeId(Source.Commit) ||
            Source.Checkout is null || !Path.IsPathFullyQualified(Source.Checkout) ||
            Source.Branch is { Length: 0 } || Components is not { Count: > 0 } || Schema is null)
            throw new InvalidDataException("发布清单缺少有效的版本、来源或组件。");
        foreach (var (name, component) in Components)
            if (!Orisumi.Core.Instance.IsName(name) || !Platforms.Runs(name, rid) ||
                component is null || !IsTreeId(component.Identity) ||
                component.Version != $"{Version ?? "dev"}+{component.Identity}" || component.Files is not { Count: > 0 })
                throw new InvalidDataException($"发布清单的组件无效：{name}");
    }

    private static bool IsTreeId(string? value) => value is { Length: 40 or 64 } && value.All(char.IsAsciiHexDigit);

    internal static Dictionary<string, string> HashFiles(string directory)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        AddFiles(new DirectoryInfo(directory));
        return new(files, StringComparer.Ordinal);

        void AddFiles(DirectoryInfo current)
        {
            foreach (var entry in current.EnumerateFileSystemInfos())
            {
                if (entry.LinkTarget is not null)
                    throw new InvalidDataException($"发布目录内包含符号链接：{entry.FullName}");
                if (entry is DirectoryInfo child) AddFiles(child);
                else files.Add(Path.GetRelativePath(directory, entry.FullName), HashFile(entry.FullName));
            }
        }
    }

    internal static string HashFile(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }
}

internal sealed record SchemaTable(
    [property: JsonRequired] string Name,
    [property: JsonRequired] SchemaColumn[] Columns);

internal sealed record SchemaColumn(
    [property: JsonRequired] string Name,
    [property: JsonRequired] string Type,
    [property: JsonRequired, JsonPropertyName("notnull")] long NotNull,
    [property: JsonRequired] string? Default,
    [property: JsonRequired] long Pk);

internal sealed record ReleasePackage(
    [property: JsonRequired] string Name,
    [property: JsonRequired] string Sha256);

internal sealed record ReleaseSource(
    [property: JsonRequired] string Commit,
    [property: JsonRequired] string Checkout,
    [property: JsonRequired] string? Branch,
    [property: JsonRequired] bool Clean);

internal sealed record ReleaseComponent(
    [property: JsonRequired] string Identity,
    [property: JsonRequired] string Version,
    [property: JsonRequired] Dictionary<string, string> Files)
{
    internal void VerifyFiles(string directory)
    {
        var actual = ReleaseManifest.HashFiles(directory);
        foreach (var (path, hash) in actual)
            if (!Files.TryGetValue(path, out var expected) || !hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"发布文件的 SHA-256 不匹配：{Path.Combine(directory, path)}");
        if (actual.Count != Files.Count)
            throw new InvalidDataException($"发布目录缺少清单中的文件：{directory}");
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(ReleaseManifest))]
[JsonSerializable(typeof(SchemaTable[]))]
internal sealed partial class ReleaseJson : JsonSerializerContext;
