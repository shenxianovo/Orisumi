using System.Runtime.InteropServices;
using System.Text;

namespace Orisumi.Platform;

public sealed partial record HostIdentity
{
    public static partial HostIdentity Read() =>
        new("macos", ReadHardwareUuid(), Sysctl("kern.hostname"), Sysctl("hw.model"), Sysctl("kern.osproductversion"),
            Sysctl("kern.osversion"), Sysctl("kern.bootsessionuuid"), getuid(), Environment.UserName);

    // gethostuuid returns IOPlatformUUID, formatted the way the system shows it.
    private static string? ReadHardwareUuid()
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
