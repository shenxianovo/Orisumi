namespace Orisumi.Collectors.Storage;

internal static class SqliteFile
{
    internal static void CreateIfMissing(string path)
    {
        // Opening an existing file through FileStream can conflict with SQLite's locks.
        if (File.Exists(path)) return;
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.ReadWrite };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try { using var file = File.Open(path, options); }
        catch (IOException) when (File.Exists(path)) { } // Another writer created the file first.
    }
}
