using Orisumi.Platform;

namespace Orisumi.Dev;

internal static class ExtensionBuild
{
    internal static async Task<int> BuildDevelopment(string checkout, CancellationToken token)
    {
        var instance = Instance.Development(checkout);
        var target = ChromeExtension.DirectoryFor(instance, checkout);
        var code = await Build(checkout, target, token);
        if (code != 0) return code;
        ChromeExtension.Configure(target, instance);
        Console.WriteLine($"Load unpacked Chrome extension: {target}");
        Console.WriteLine($"Expected Chrome extension ID: {ChromeExtension.Id(target)}");
        return 0;
    }

    internal static async Task<int> Build(string checkout, string target, CancellationToken token)
    {
        var source = Path.Combine(checkout, DevCommand.ExtensionDirectory);
        var code = await Development.Run("npm", source, token, "run", "build", "--", "--outDir", target);
        if (code == 0) File.Copy(Path.Combine(source, "manifest.json"), Path.Combine(target, "manifest.json"), overwrite: true);
        return code;
    }
}
