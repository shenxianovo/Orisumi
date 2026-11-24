using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Orisumi.Capture.Storage;

internal static partial class FileIdentity
{
    internal static partial string Of(FileStream stream)
    {
        const int fileIdInfoClass = 18;
        if (!GetFileInformationByHandleEx(stream.SafeFileHandle, fileIdInfoClass, out var info, Marshal.SizeOf<FileIdInfo>()))
            throw new IOException($"could not identify {stream.Name}", new Win32Exception(Marshal.GetLastPInvokeError()));
        return $"{info.Volume:X16}:{info.IdLow:X16}{info.IdHigh:X16}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong Volume;
        public ulong IdLow;
        public ulong IdHigh;
    }

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileIdInfo info, int size);
}
