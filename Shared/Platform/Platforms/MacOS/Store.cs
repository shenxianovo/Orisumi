namespace Orisumi.Platform;

public sealed partial class Store
{
    internal static partial string HostExecutableIn(string release) =>
        Path.Combine(release, "Orisumi.app", "Contents", "MacOS", ProgramDescription.ExecutableName("host", Platforms.CurrentRid));

    private static partial void CreateDatabaseFile(string path)
    {
        using var file = File.Open(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
    }
}
