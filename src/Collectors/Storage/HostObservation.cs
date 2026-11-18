using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Orisumi.Collectors.Storage;

// The host a capture run starts on, as the platform reports it. A row is added only when a value changes;
// which device these values belong to is read out later (devices.mdx). ForegroundDatabase.swift reads the same values.
internal static class HostObservation
{
    // Returns the observation the run refers to; call it in the transaction that inserts the run.
    internal static long Record(SqliteCommand command, string store)
    {
        object?[] values =
        [
            HardwareUuid(), Sysctl("kern.hostname"), Sysctl("hw.model"), Sysctl("kern.osproductversion"),
            Sysctl("kern.osversion"), Sysctl("kern.bootsessionuuid"), (long)getuid(), Environment.UserName,
            Path.GetFullPath(store), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        ];
        if (command.Scalar("""
                SELECT observation_id FROM host_observations
                WHERE observation_id = (SELECT MAX(observation_id) FROM host_observations)
                  AND hardware_uuid IS $p0 AND host_name IS $p1 AND model IS $p2 AND os_version IS $p3 AND os_build IS $p4
                  AND boot_id IS $p5 AND user_id IS $p6 AND user_name IS $p7 AND store_path IS $p8
                """, values) is long latest)
            return latest;
        return (long)command.Scalar("""
            INSERT INTO host_observations(observed_at_utc_ms,platform,hardware_uuid,host_name,model,os_version,os_build,boot_id,user_id,user_name,store_path)
            VALUES ($p9,'macos',$p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8) RETURNING observation_id
            """, values)!;
    }

    // gethostuuid returns IOPlatformUUID, formatted the way the system shows it.
    private static string? HardwareUuid()
    {
        var bytes = new byte[16];
        var wait = new Timespec { Seconds = 1 };
        if (gethostuuid(bytes, ref wait) != 0) return null;
        var hex = Convert.ToHexString(bytes);
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    private static string? Sysctl(string name)
    {
        nint length = 0;
        if (sysctlbyname(name, null, ref length, 0, 0) != 0 || length == 0) return null;
        var value = new byte[length];
        if (sysctlbyname(name, value, ref length, 0, 0) != 0) return null;
        return Encoding.UTF8.GetString(value, 0, (int)length).TrimEnd('\0');
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec { internal long Seconds; internal long Nanoseconds; }
    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern int gethostuuid(byte[] uuid, ref Timespec wait);
    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern uint getuid();
    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern int sysctlbyname(string name, byte[]? value, ref nint length, nint newValue, nint newLength);
}
