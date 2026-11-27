#:project ../Shared/Capture/Orisumi.Capture.csproj
#:property PublishAot=false

using System.Text.Json;
using Orisumi.Capture.Hosting;
using Orisumi.Capture.Storage;
using Orisumi.Platform;

var host = HostIdentity.Read();
if (OperatingSystem.IsMacOS())
{
    Check(host is { Platform: "macos", HardwareUuid.Length: 36, BootId: not null, UserId: not null, UserName.Length: > 0,
        MachineGuid: null, UserSid: null }, "macOS identity reads the hardware UUID, boot session and user");
}
else
{
    Check(host is { Platform: "windows", UserId: null, UserSid.Length: > 0, MachineGuid.Length: > 0, UserName.Length: > 0 },
        "Windows identity reads MachineGuid and the user SID");
}

var withPlaceholder = host with { Platform = "windows", HardwareUuid = Guid.Empty.ToString(), MachineGuid = "machine-A" };
Check(withPlaceholder.MatchesDatabase("windows", null, "machine-A") && !withPlaceholder.MatchesDatabase("windows", Guid.Empty.ToString(), "machine-B"),
    "a placeholder SMBIOS UUID falls back to MachineGuid");
Console.WriteLine($"PASS: {host.Platform} host identity (hardware UUID {host.HardwareUuid ?? "unknown"})");

var temp = Directory.CreateTempSubdirectory("orisumi-platform-");
try
{
    var store = new Store(temp.FullName);
    Check(store.CurrentDatabase("codex") is null && !Directory.Exists(store.State), "reading never starts a database");

    var (path, id, platform, hardwareUuid, machineGuid) = store.WritableDatabase("codex");
    Check(platform == host.Platform && hardwareUuid == host.HardwareUuid && machineGuid == host.MachineGuid,
        "the new database reports its bound host identity");
    Check(Path.GetFileName(path) == id + ".sqlite" && Path.GetDirectoryName(path) == store.Observations("codex") && new FileInfo(path).Length == 0,
        "a new database is an empty file named by its ID under observations/<source>/");
    if (OperatingSystem.IsMacOS())
        Check(File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "a new Mac database is private");
    Check(store.WritableDatabase("codex").DatabaseId == id && store.CurrentDatabase("codex") == path, "the current database is reused");

    var pointer = Path.Combine(store.State, "current", "codex.json");
    File.WriteAllText(pointer, JsonSerializer.Serialize(new { databaseId = id, hardwareUuid }));
    Check((store.CurrentDatabase("codex") == path) == OperatingSystem.IsMacOS(), "a legacy pointer belongs to macOS");
    if (OperatingSystem.IsMacOS()) Check(store.WritableDatabase("codex").DatabaseId == id, "a legacy macOS pointer reopens its database");

    var other = OperatingSystem.IsMacOS() ? "windows" : "macos";
    File.WriteAllText(pointer, JsonSerializer.Serialize(new { databaseId = id, platform = other, hardwareUuid, machineGuid }));
    Check(store.CurrentDatabase("codex") is null && store.Databases("codex").Contains(path), "a foreign database remains readable as a copy");
    var moved = store.WritableDatabase("codex").DatabaseId;
    Check(moved != id && File.Exists(path) && store.Databases("codex").Count == 2, "a foreign pointer starts a new database");

    File.WriteAllText(pointer, JsonSerializer.Serialize(new { databaseId = moved, platform, hardwareUuid = "different-uuid", machineGuid = "different-guid" }));
    Check(store.WritableDatabase("codex").DatabaseId != moved, "changed machine identity starts a new database");
    var previous = store.CurrentDatabase("codex")!;
    File.Delete(previous);
    Check(store.WritableDatabase("codex").Path != previous, "a deleted database is never recreated under its old ID");

    var ids = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => store.WritableDatabase("browser").DatabaseId)));
    Check(ids.Distinct().Count() == 1 && store.Databases("browser").Count == 1, "concurrent first opens start one database");
    Console.WriteLine("PASS: database pointers across hosts, legacy pointers, deleted files and concurrent first opens");

    using (var database = CollectorDatabase.Open(store.Root, new Collector("fixture", typeof(Program).Assembly)))
    using (var command = database.CreateCommand())
    {
        Check(command.Scalar("""
            SELECT COUNT(*) FROM database_identity WHERE platform = $p0 AND hardware_uuid IS $p1 AND machine_guid IS $p2
            """, platform, hardwareUuid, machineGuid) is 1L, "database identity preserves the raw host keys");
        using var transaction = database.BeginTransaction();
        command.Transaction = transaction;
        var observation = HostObservation.Record(command, store.Root);
        Check(HostObservation.Record(command, store.Root) == observation && command.Scalar("""
            SELECT COUNT(*) FROM host_observations WHERE observation_id = $p0 AND user_id IS $p1 AND user_sid IS $p2
                AND hardware_uuid IS $p3 AND machine_guid IS $p4
            """, observation, host.UserId, host.UserSid, hardwareUuid, machineGuid) is 1L,
            "unchanged host keys and user identities share an observation");
        Check(command.Execute("INSERT OR IGNORE INTO host_observations(observed_at_utc_ms,platform,store_path) VALUES(1,$p0,$p1)", other, store.Root) == 0,
            "the packaged schema rejects the other platform");
        transaction.Commit();
    }
    Console.WriteLine("PASS: database identity, host observations and platform constraint");

    var log = Path.Combine(temp.FullName, "log.jsonl");
    var renamed = log + ".old";
    File.WriteAllText(log, "{\"n\":1}\n");
    using var reader = new JsonlReader(log);
    File.Move(log, renamed);
    File.WriteAllText(log, "{\"n\":2}\n");
    Check(JsonlReader.Identify(renamed) == reader.Identity && JsonlReader.Identify(log) != reader.Identity,
        "file identity survives rename and changes on replacement while the old file is open");
    Console.WriteLine("PASS: open file identity");
}
finally { temp.Delete(recursive: true); }

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
