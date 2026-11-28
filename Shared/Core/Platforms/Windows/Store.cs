namespace Orisumi.Core;

public sealed partial class Store
{
    internal static partial string HostExecutableIn(string release) => Path.Combine(release, ProgramDescription.ExecutableName("host", Platforms.CurrentRid));

    private static partial void CreateDatabaseFile(string path)
    {
        // Windows applies the store directory's inheritable access control entries at creation.
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    }
}
