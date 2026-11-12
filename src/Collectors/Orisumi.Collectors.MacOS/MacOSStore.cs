namespace Orisumi.Collectors.MacOS;

internal static class MacOSStore
{
    internal static string GetDatabasePath(string store) => Path.GetFullPath(Path.Combine(store, "raw", "macos.sqlite"));
}
