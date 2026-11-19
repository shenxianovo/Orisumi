#:project ../src/Orisumi.Platform/Orisumi.Platform.csproj
#:property PublishAot=false

using System.Text.Json;
using Orisumi.Platform;

if (!OperatingSystem.IsMacOS())
{
    Console.WriteLine("Skipped: the platform layer implements macOS only.");
    return;
}

var host = HostIdentity.Read();
Check(host.Platform == "macos" && host.HardwareUuid is { Length: 36 } && host.BootId is not null && host.UserName.Length > 0,
    "macOS host identity reads the hardware UUID, boot session and user");
Console.WriteLine("PASS: host identity");

var temp = Directory.CreateTempSubdirectory("orisumi-platform-");
try
{
    var store = new Store(temp.FullName);
    Check(store.CurrentDatabase("codex") is null && !Directory.Exists(store.State), "reading never starts a database");

    var (path, id) = store.WritableDatabase("codex");
    Check(Path.GetFileName(path) == id + ".sqlite" && Path.GetDirectoryName(path) == store.Observations("codex") && new FileInfo(path).Length == 0,
        "a new database is an empty file named by its ID under observations/<source>/");
    Check((File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0, "a new database is private");
    Check(store.WritableDatabase("codex").DatabaseId == id && store.CurrentDatabase("codex") == path, "the current database is reused");

    // A store copied to other hardware starts its own database and keeps the copied one.
    var pointer = Path.Combine(store.State, "current", "codex.json");
    File.WriteAllText(pointer, JsonSerializer.Serialize(new { databaseId = id, hardwareUuid = "00000000-0000-0000-0000-000000000000" }));
    var moved = store.WritableDatabase("codex").DatabaseId;
    Check(moved != id && File.Exists(path) && store.Databases("codex").Count == 2, "a pointer from other hardware starts a new database");

    // A deleted database is never recreated under its old ID.
    File.Delete(store.CurrentDatabase("codex")!);
    var replaced = store.WritableDatabase("codex").DatabaseId;
    Check(replaced != moved && store.Databases("codex").Count == 2, "a missing database file starts a new database");

    // Chrome starts one native host per message; concurrent first opens agree on one database.
    var ids = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => store.WritableDatabase("browser").DatabaseId)));
    Check(ids.Distinct().Count() == 1 && store.Databases("browser").Count == 1, "concurrent first opens start one database");
    Console.WriteLine("PASS: store layout, host binding, missing files and concurrent first opens");
}
finally { temp.Delete(recursive: true); }

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
