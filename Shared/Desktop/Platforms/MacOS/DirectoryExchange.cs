using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Orisumi.Desktop;

internal static class DirectoryExchange
{
    [SupportedOSPlatform("macos")]
    internal static void Exchange(string from, string to)
    {
        // Darwin can exchange two nonempty directories without a gap at the installed path.
        if (renamex_np(from, to, 2) != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"无法替换 {to}");
    }

    [SupportedOSPlatform("macos")]
    [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
    private static extern int renamex_np([MarshalAs(UnmanagedType.LPUTF8Str)] string from,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string to, uint flags);
}
