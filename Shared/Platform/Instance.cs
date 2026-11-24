using System.Text.Json;

namespace Orisumi.Platform;

public sealed partial class Instance
{
    private const string FileName = "instance.json";

    private Instance(string root, string name)
    {
        Store = new Store(root);
        if (!IsName(name)) throw new ArgumentException($"无效的实例名：{name}，仅允许 [a-z0-9_]+。");
        Name = name;
    }

    public Store Store { get; }
    public string Name { get; }
    public int BrowserPort => Name switch
    {
        "prod" => 47810,
        "dev" => 47811,
        _ => 47900 + (int)(Name.Aggregate(2166136261u,
            (hash, character) => unchecked((hash ^ character) * 16777619u)) % 100),
    };

    public static Instance Open(string root)
    {
        var path = Path.Combine(root, FileName);
        if (!File.Exists(path)) throw new FileNotFoundException($"缺少实例配置：{path}");
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        if (json.RootElement.ValueKind != JsonValueKind.Object ||
            !json.RootElement.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"实例配置缺少 name：{path}");
        return new(root, name.GetString()!);
    }

    public static Instance Create(string root, string name)
    {
        var instance = new Instance(root, name);
        var path = Path.Combine(instance.Store.Root, FileName);
        Directory.CreateDirectory(instance.Store.Root);
        if (!File.Exists(path))
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, $"{{\"name\":\"{name}\"}}\n");
                try { File.Move(temporary, path); }
                catch (IOException) when (File.Exists(path)) { }
            }
            finally { File.Delete(temporary); }
        }
        var existing = Open(root);
        if (existing.Name != name) throw new ArgumentException($"{path} 的实例名是 {existing.Name}，预期 {name}。");
        return existing;
    }

    public static Instance? Find(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, FileName)))
                return Open(directory.FullName);
        return null;
    }

    public static string DefaultRoot() => Resolve(AppContext.BaseDirectory).Store.Root;

    public static Instance Resolve(string baseDirectory) => Find(baseDirectory) ??
        throw new DirectoryNotFoundException("找不到 instance.json。请使用 --store 指定存储目录。");

    public static string ProductionRoot() => ApplicationSupport("Orisumi");
    public static string DevelopmentRoot() => ApplicationSupport("Orisumi-dev");

    private static partial string ApplicationSupport(string folder);

    internal static bool IsName(string name) => name.Length > 0 && name.All(IsNameCharacter);
    private static bool IsNameCharacter(char character) => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_';
}
