using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orisumi.Platform;

// A store's directories and the database each source writes; overview/storage.mdx describes the layout and rules.
// No other code composes paths inside a store.
public sealed partial class Store(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public string State => Path.Combine(Root, "state");
    public string Logs => Path.Combine(Root, "logs");

    public string App => Path.Combine(Root, "app");
    internal string AppLock => Path.Combine(App, ".lock");
    internal string Manifest => Path.Combine(App, ReleaseManifest.FileName);
    public string Host => Path.Combine(App, "host");
    internal string HostExecutable => HostBundle.Executable(Host);
    public string Collectors => Path.Combine(App, "collectors");
    public string Collector(string name) => Path.Combine(Collectors, name);

    internal string ObservationsRoot => Path.Combine(Root, "observations");
    internal string TitleRules => Path.Combine(Root, "rules", "titles.json");
    internal string Backups => Path.Combine(Root, "backups");

    public string Observations(string source) => Path.Combine(ObservationsRoot, source);

    public IEnumerable<string> Sources() => Directory.Exists(ObservationsRoot)
        ? Directory.EnumerateDirectories(ObservationsRoot).Select(Path.GetFileName).OfType<string>()
        : [];

    public string LockFile(string name) => Path.Combine(State, "locks", name + ".lock");

    // Every database of the source here: the one written now, earlier ones and copies from other devices.
    public IReadOnlyList<string> Databases(string source)
    {
        var directory = Observations(source);
        return Directory.Exists(directory) ? Directory.GetFiles(directory, "*.sqlite").Order(StringComparer.Ordinal).ToArray() : [];
    }

    // The database the source writes on this host, or null before its first capture. Reading never starts one.
    public string? CurrentDatabase(string source) =>
        ReadPointer(source) is { } pointer && Database(source, pointer.DatabaseId) is var path && File.Exists(path) ? path : null;

    // The database the source writes on this host, as an existing or new empty file named by its database ID.
    // A new one starts when there is none, its file is gone, or the pointer was written on other hardware,
    // such as a store copied to a new Mac. Unknown hardware cannot prove a move, so it keeps the current one.
    // Also returns the host the database is bound to, which the collector records when it creates the database.
    public (string Path, string DatabaseId, string Platform, string? HardwareUuid) WritableDatabase(string source)
    {
        using var guard = WaitLock(LockFile(source));
        var host = HostIdentity.Read();
        if (ReadPointer(source) is { } pointer && File.Exists(Database(source, pointer.DatabaseId)) &&
            (host.HardwareUuid is null || pointer.HardwareUuid == host.HardwareUuid))
            return (Database(source, pointer.DatabaseId), pointer.DatabaseId, host.Platform, pointer.HardwareUuid);

        var databaseId = Guid.NewGuid().ToString("N");
        var path = Database(source, databaseId);
        Directory.CreateDirectory(Observations(source));
        // Created under the lock, so a concurrent caller sees the file and does not start another database.
        using (File.Open(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                   UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite })) { }
        var pointerPath = PointerFile(source);
        Directory.CreateDirectory(Path.GetDirectoryName(pointerPath)!);
        File.WriteAllText(pointerPath + ".tmp", JsonSerializer.Serialize(new Pointer(databaseId, host.HardwareUuid), PointerJson.Default.Pointer));
        File.Move(pointerPath + ".tmp", pointerPath, overwrite: true);
        return (path, databaseId, host.Platform, host.HardwareUuid);
    }

    private string Database(string source, string databaseId) => Path.Combine(Observations(source), databaseId + ".sqlite");

    private string PointerFile(string source) => Path.Combine(State, "current", source + ".json");

    private Pointer? ReadPointer(string source)
    {
        try
        {
            var pointer = JsonSerializer.Deserialize(File.ReadAllText(PointerFile(source)), PointerJson.Default.Pointer);
            return pointer is { DatabaseId: { Length: 32 } id } && Guid.TryParseExact(id, "N", out _) ? pointer : null;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or JsonException) { return null; }
    }

    // Concurrent writers wait for short store operations such as selecting the current database.
    internal static FileStream WaitLock(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        for (var waited = Stopwatch.StartNew(); ; Thread.Sleep(20))
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (waited.Elapsed < TimeSpan.FromSeconds(10)) { }
        }
    }

    private sealed record Pointer(string DatabaseId, string? HardwareUuid);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(Pointer))]
    private sealed partial class PointerJson : JsonSerializerContext;
}
