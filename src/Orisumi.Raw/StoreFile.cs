using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Orisumi.Raw;

internal static partial class StoreFile
{
    private const int PosixFileExists = 17;
    private const int WindowsFileExists = 80;
    private const int WindowsAlreadyExists = 183;

    public static bool Create(string path, ReadOnlySpan<byte> bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pending = $"{path}.{Guid.NewGuid():N}.pending";
        try
        {
            using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                file.Write(bytes);
            return Publish(pending, path);
        }
        finally
        {
            File.Delete(pending);
        }
    }

    private static bool Publish(string pending, string path)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                File.Move(pending, path);
                return true;
            }
            catch (IOException error) when ((error.HResult & 0xFFFF) is WindowsFileExists or WindowsAlreadyExists && File.Exists(path))
            {
                return false;
            }
        }
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("原件原子发布当前支持 Windows、macOS 与 Linux");

        // Unix File.Move checks then renames; link atomically publishes without replacing a concurrent winner.
        if (Link(pending, path) == 0) return true;
        var code = Marshal.GetLastPInvokeError();
        if (code == PosixFileExists && File.Exists(path)) return false;
        var nativeError = new Win32Exception(code);
        throw new IOException($"无法原子发布原件 {path}（errno {code}）：{nativeError.Message}", nativeError);
    }

    [LibraryImport("libc", EntryPoint = "link", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Link(string existingPath, string newPath);
}
