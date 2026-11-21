using System.Text.Json;
using System.Text.Json.Nodes;

namespace Orisumi.Dev;

internal static class ExtensionBuild
{
    internal static async Task<int> Build(string checkout, string target, string version, CancellationToken token)
    {
        var source = Path.Combine(checkout, DevCommand.ExtensionDirectory);
        var code = await Development.Run("npm", source, token, "run", "build", "--", "--outDir", target);
        if (code == 0)
        {
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(source, "manifest.json"), token))!.AsObject();
            manifest["version_name"] = version;
            await File.WriteAllTextAsync(Path.Combine(target, "manifest.json"),
                manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", token);
        }
        return code;
    }
}
