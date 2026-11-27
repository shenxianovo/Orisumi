internal static class TemporaryGit
{
    public static void Delete(DirectoryInfo root)
    {
        if (OperatingSystem.IsWindows())
            foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories))
                if (file.IsReadOnly) file.IsReadOnly = false;
        root.Delete(recursive: true);
    }
}
