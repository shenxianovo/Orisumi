using System.Text.Json;
using Orisumi.Platform;

namespace Orisumi.Dev;

internal static class ComponentIdentity
{
    internal static async Task<string> Read(string checkout, string componentDirectory, string project, string rid, string dotnet,
        CancellationToken token)
    {
        var inputs = new HashSet<string>(StringComparer.Ordinal) { Relative(componentDirectory) };
        var projects = new HashSet<string>(StringComparer.Ordinal);
        await AddProject(Path.GetFullPath(project, checkout));

        var temporary = Directory.CreateTempSubdirectory("orisumi-index-");
        try
        {
            var index = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = Path.Combine(temporary.FullName, "index") };
            List<string> pathspecs =
            [
                .. inputs.Order(StringComparer.Ordinal).Select(input => ":(top,literal)" + input),
                ":(top,exclude,glob)**/Tests/**",
                ":(top,exclude,glob)**/test/**",
                ":(top,exclude,glob)**/Platforms/" + (Platforms.Name(rid) == "macos" ? "Windows" : "MacOS") + "/**",
            ];
            if (Platforms.Name(rid) == "windows") pathspecs.Add(":(top,exclude)Host/App.MacOS");
            await Git(index, ["add", "-A", "--", .. pathspecs]);
            return (await Git(index, ["write-tree"])).Trim();
        }
        finally { temporary.Delete(recursive: true); }

        Task<string> Git(IReadOnlyDictionary<string, string> index, string[] arguments) =>
            Repository.Query("git", checkout, token, arguments, index);

        async Task AddProject(string path)
        {
            if (!projects.Add(path)) return;
            var directory = Path.GetDirectoryName(path)!;
            inputs.Add(Relative(directory));
            for (var parent = new DirectoryInfo(directory); parent is not null && DirectoryPaths.Contains(checkout, parent.FullName);
                parent = parent.Parent)
            {
                foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets" })
                {
                    var file = Path.Combine(parent.FullName, name);
                    if (File.Exists(file)) inputs.Add(Relative(file));
                }
            }
            var references = await Repository.Query(dotnet, checkout, token,
                ["msbuild", path, "-nologo", "-p:Configuration=Release", $"-p:RuntimeIdentifier={rid}",
                    "-p:PublishAot=true", "-getItem:ProjectReference"]);
            using var json = JsonDocument.Parse(references);
            foreach (var reference in json.RootElement.GetProperty("Items").GetProperty("ProjectReference").EnumerateArray())
                await AddProject(reference.GetProperty("FullPath").GetString()!);
        }

        string Relative(string path)
        {
            var full = Path.GetFullPath(path, checkout);
            if (!DirectoryPaths.Contains(checkout, full))
                throw new InvalidDataException($"组件输入必须位于检出内：{full}");
            return Path.GetRelativePath(checkout, full).Replace(Path.DirectorySeparatorChar, '/');
        }
    }
}
