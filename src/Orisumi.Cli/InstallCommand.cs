using System.ComponentModel;
using System.Runtime.InteropServices;
using Orisumi.Platform;

namespace Orisumi.Cli;

internal static class InstallCommand
{
    internal static async Task Run(Instance instance, string[] directories, SystemLocations locations, TextWriter output,
        CancellationToken token)
    {
        var store = instance.Store;
        using var guard = Store.WaitLock(store.LockFile("app"));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in directories)
        {
            token.ThrowIfCancellationRequested();
            var source = DirectoryPaths.Resolve(directory);
            var app = DirectoryPaths.Resolve(store.App);
            if (DirectoryPaths.Contains(app, source) || DirectoryPaths.Contains(source, app))
                throw new ArgumentException($"发布目录必须位于实例的 app 之外：{source}");
            var executables = Directory.GetFiles(source)
                .Where(IsProgram).ToArray();
            if (executables is not [var executable])
                throw new InvalidDataException($"发布目录应包含一个 Orisumi 程序：{source}");
            var filename = Path.GetFileName(executable);
            var name = filename == "orisumi" ? "host" : filename[8..];
            if (ProgramDescription.ExecutableName(name) != filename || !seen.Add(name))
                throw new InvalidDataException($"无效或重复的发布组件：{executable}");

            var destination = name == "host" ? store.Host : store.Collector(name);
            var temporary = destination + "." + Guid.NewGuid().ToString("N");
            try
            {
                Copy(source, temporary);
                var description = await ProgramDescription.Read(Path.Combine(temporary, filename), [], token);
                if (description.Name != name)
                    throw new InvalidDataException($"{executable}: expected {name}, found {description.Name}");
                if (name == "browser") ChromeExtension.Configure(Path.Combine(temporary, "extension"), instance);
                if (Directory.Exists(destination))
                {
                    // Darwin can exchange two nonempty directories without a gap at the installed path.
                    if (renamex_np(temporary, destination, 2) != 0)
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), $"无法替换 {destination}");
                }
                else Directory.Move(temporary, destination);

                if (name == "host" && !instance.Name.StartsWith("dev_", StringComparison.Ordinal))
                    Link(locations.Commands, instance.CommandName, Path.Combine(destination, "orisumi"));
                output.WriteLine($"Installed {name} {description.Version} in {store.Root}");
            }
            finally
            {
                if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
            }
        }
    }

    private static bool IsProgram(string path)
    {
        var name = Path.GetFileName(path);
        return (name == "orisumi" || (name.StartsWith("orisumi-", StringComparison.Ordinal) && Instance.IsName(name[8..]))) &&
            (File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0;
    }

    private static void Copy(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            var path = Path.Combine(destination, entry.Name);
            if (entry.LinkTarget is not null) throw new InvalidDataException($"发布目录内包含符号链接：{entry.FullName}");
            if (entry is DirectoryInfo) Copy(entry.FullName, path);
            else File.Copy(entry.FullName, path);
        }
    }

    private static void Link(string directory, string name, string target)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.CreateSymbolicLink(temporary, target);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
    private static extern int renamex_np([MarshalAs(UnmanagedType.LPUTF8Str)] string from,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string to, uint flags);
}
