using System.Runtime.InteropServices;

namespace Orisumi.Capture.Storage;

internal static partial class FileIdentity
{
    // Device, inode and birth time survive a move and change on replacement.
    internal static partial string Of(FileStream stream)
    {
        if (fstat(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), out var stat) != 0)
            throw new IOException($"could not identify {stream.Name}", new System.ComponentModel.Win32Exception());
        return $"{stat.Device}:{stat.Inode}:{stat.BirthSeconds}:{stat.BirthNanoseconds}";
    }

    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct DarwinStat
    {
        [FieldOffset(0)] public int Device;
        [FieldOffset(8)] public ulong Inode;
        [FieldOffset(80)] public long BirthSeconds;
        [FieldOffset(88)] public long BirthNanoseconds;
    }
    [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
    private static extern int fstat(int descriptor, out DarwinStat stat);
}
