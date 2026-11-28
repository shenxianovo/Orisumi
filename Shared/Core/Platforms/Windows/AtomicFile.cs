using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Orisumi.Core;

public static partial class AtomicFile
{
    // MoveFileEx (File.Move) can't replace a file another process has open, even one that allows deletion: the
    // supervisor exited when `capture status` was reading status.json. A POSIX-semantics rename can (NTFS, Windows 10 1809+).
    public static partial void Replace(string source, string destination)
    {
        using var handle = CreateFileW(Path.GetFullPath(source), 0x00010000 /* DELETE */, 7 /* share read, write, delete */,
            0, 3 /* OPEN_EXISTING */, 0, 0);
        if (handle.IsInvalid) throw Error(source);
        // FILE_RENAME_INFO on x64: flags at 0, root directory at 8, name length in bytes at 16, name from 20.
        var name = Encoding.Unicode.GetBytes(Path.GetFullPath(destination));
        var info = new byte[20 + name.Length + 2];
        BitConverter.TryWriteBytes(info.AsSpan(0), 0x1u | 0x2u); // REPLACE_IF_EXISTS | POSIX_SEMANTICS
        BitConverter.TryWriteBytes(info.AsSpan(16), (uint)name.Length);
        name.CopyTo(info, 20);
        if (!SetFileInformationByHandle(handle, 22 /* FileRenameInfoEx */, info, (uint)info.Length)) throw Error(destination);
    }

    private static IOException Error(string path) => new($"{path}: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition,
        uint flags, nint template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, byte[] info, uint size);
}
