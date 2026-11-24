namespace Orisumi.Platform;

public sealed partial class Instance
{
    private static partial string ApplicationSupport(string folder) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), folder);
}
