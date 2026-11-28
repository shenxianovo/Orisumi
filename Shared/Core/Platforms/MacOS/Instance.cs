namespace Orisumi.Core;

public sealed partial class Instance
{
    private static partial string ApplicationSupport(string folder) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", folder);
}
