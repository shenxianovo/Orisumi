using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Orisumi.Cli.Capture;

// Darwin's posix_spawn keeps the caller's security context and opens the log files
// in the child. No pipe or managed copy task depends on the startup command staying alive.
internal static class DetachedProcess
{
    internal static int Start(string[] command, string stdout, string stderr)
    {
        const int append = 0x1 | 0x8 | 0x200; // O_WRONLY | O_APPEND | O_CREAT
        Check(posix_spawn_file_actions_init(out var files));
        try
        {
            Check(posix_spawn_file_actions_addopen(ref files, 0, "/dev/null", 0, 0));
            Check(posix_spawn_file_actions_addopen(ref files, 1, stdout, append, 0x180)); // 0600
            Check(posix_spawn_file_actions_addopen(ref files, 2, stderr, append, 0x180));
            Check(posix_spawnattr_init(out var attributes));
            try
            {
                Check(posix_spawnattr_setflags(ref attributes, 0x2)); // POSIX_SPAWN_SETPGROUP
                Check(posix_spawnattr_setpgroup(ref attributes, 0));
                // nohup only sets SIGHUP disposition, then execs the command with the same PID.
                using var arguments = new NativeStrings(["/usr/bin/nohup", .. command]);
                using var environment = new NativeStrings(Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
                    .Select(entry => $"{entry.Key}={entry.Value}"));
                Check(posix_spawn(out var pid, "/usr/bin/nohup", ref files, ref attributes, arguments.Pointer, environment.Pointer));
                return pid;
            }
            finally { posix_spawnattr_destroy(ref attributes); }
        }
        finally { posix_spawn_file_actions_destroy(ref files); }
    }

    internal static bool HasExited(int pid) => waitpid(pid, out _, 1) == pid; // WNOHANG, reaps a failed startup

    private static void Check(int error)
    {
        if (error != 0) throw new Win32Exception(error);
    }

    private sealed class NativeStrings : IDisposable
    {
        private readonly IntPtr[] strings;
        internal IntPtr Pointer { get; }
        internal NativeStrings(IEnumerable<string> values)
        {
            strings = values.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
            Pointer = Marshal.AllocHGlobal((strings.Length + 1) * IntPtr.Size);
            Marshal.Copy(strings, 0, Pointer, strings.Length);
            Marshal.WriteIntPtr(Pointer, strings.Length * IntPtr.Size, IntPtr.Zero);
        }
        public void Dispose()
        {
            foreach (var value in strings) Marshal.FreeCoTaskMem(value);
            Marshal.FreeHGlobal(Pointer);
        }
    }

    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    [DllImport(LibSystem)] private static extern int posix_spawn_file_actions_init(out IntPtr actions);
    [DllImport(LibSystem)] private static extern int posix_spawn_file_actions_destroy(ref IntPtr actions);
    [DllImport(LibSystem)] private static extern int posix_spawn_file_actions_addopen(ref IntPtr actions, int fd,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, ushort mode);
    [DllImport(LibSystem)] private static extern int posix_spawnattr_init(out IntPtr attributes);
    [DllImport(LibSystem)] private static extern int posix_spawnattr_destroy(ref IntPtr attributes);
    [DllImport(LibSystem)] private static extern int posix_spawnattr_setflags(ref IntPtr attributes, short flags);
    [DllImport(LibSystem)] private static extern int posix_spawnattr_setpgroup(ref IntPtr attributes, int group);
    [DllImport(LibSystem)] private static extern int posix_spawn(out int pid,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, ref IntPtr actions, ref IntPtr attributes, IntPtr arguments, IntPtr environment);
    [DllImport(LibSystem)] private static extern int waitpid(int pid, out int status, int options);
}
