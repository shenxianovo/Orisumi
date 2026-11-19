using System.Diagnostics;
using System.Text.Json;

namespace Orisumi.Platform;

// A store's directories and the database each source writes; storage.mdx describes the layout and rules.
// No other code composes paths inside a store.
public sealed class Store(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public string State => Path.Combine(Root, "state");
    public string Logs => Path.Combine(Root, "logs");

    public static string DefaultRoot()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Only the macOS store location is implemented.");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Orisumi");
    }

    public string Observations(string source) => Path.Combine(Root, "observations", source);

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
    public (string Path, string DatabaseId) WritableDatabase(string source)
    {
        using var guard = WaitLock(LockFile(source));
        var hardware = HostIdentity.Read().HardwareUuid;
        if (ReadPointer(source) is { } pointer && File.Exists(Database(source, pointer.DatabaseId)) &&
            (hardware is null || pointer.HardwareUuid == hardware))
            return (Database(source, pointer.DatabaseId), pointer.DatabaseId);

        var databaseId = Guid.NewGuid().ToString("N");
        var path = Database(source, databaseId);
        Directory.CreateDirectory(Observations(source));
        // Created under the lock, so a concurrent caller sees the file and does not start another database.
        using (File.Open(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                   UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite })) { }
        var pointerPath = PointerFile(source);
        Directory.CreateDirectory(Path.GetDirectoryName(pointerPath)!);
        File.WriteAllText(pointerPath + ".tmp", JsonSerializer.Serialize(new Pointer(databaseId, hardware), JsonSerializerOptions.Web));
        File.Move(pointerPath + ".tmp", pointerPath, overwrite: true);
        return (path, databaseId);
    }

    private string Database(string source, string databaseId) => Path.Combine(Observations(source), databaseId + ".sqlite");

    private string PointerFile(string source) => Path.Combine(State, "current", source + ".json");

    private Pointer? ReadPointer(string source)
    {
        try
        {
            var pointer = JsonSerializer.Deserialize<Pointer>(File.ReadAllText(PointerFile(source)), JsonSerializerOptions.Web);
            return pointer is { DatabaseId: { Length: 32 } id } && Guid.TryParseExact(id, "N", out _) ? pointer : null;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or JsonException) { return null; }
    }

    // Short critical sections from concurrent processes, such as one Chrome native host per message, wait for each other.
    private static FileStream WaitLock(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        for (var waited = Stopwatch.StartNew(); ; Thread.Sleep(20))
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (waited.Elapsed < TimeSpan.FromSeconds(10)) { }
        }
    }

    private sealed record Pointer(string DatabaseId, string? HardwareUuid);
}
