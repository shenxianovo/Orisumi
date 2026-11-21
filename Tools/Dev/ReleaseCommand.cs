using System.CommandLine;
using System.ComponentModel;
using System.Globalization;
using Orisumi.Platform;

namespace Orisumi.Dev;

internal static class ReleaseCommand
{
    internal static Command Create()
    {
        var version = new Argument<string?>("version") { Arity = ArgumentArity.ZeroOrOne };
        var store = new Option<string>("--dev-store")
        {
            Description = "开发实例的存储目录。",
            DefaultValueFactory = _ => Instance.DevelopmentRoot(),
        };
        var command = new Command("release", "构建并发布已在开发实例验证的版本。") { version, store };
        command.SetAction((result, token) => Run(Repository.Require(), result.GetValue(version), result.GetRequiredValue(store),
            RunProcess.Dotnet, Console.Out, token, HostBundle.Tools.Current));
        return command;
    }

    internal static async Task Run(string checkout, string? requested, string devRoot, string dotnet, TextWriter output,
        CancellationToken token, HostBundle.Tools host)
    {
        var source = await ReleaseBuild.Source(checkout, token);
        if (source.Branch != "main") throw new InvalidDataException("只能在 main 分支发布。");
        if (!source.Clean) throw new InvalidDataException("工作树有未提交的改动。");
        await Git("fetch", "origin", "--tags", "--prune");
        const string main = "refs/remotes/origin/main";
        if ((await Git("for-each-ref", "--format=%(refname) %(objectname)", main)).Trim() != $"{main} {source.Commit}")
            throw new InvalidDataException("HEAD 必须等于 origin/main。请先推送提交，再发布。");

        var tags = (await Git("tag", "--list")).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var previousVersion = tags.Where(tag => tag.StartsWith("v5.", StringComparison.Ordinal))
            .Select(ReleaseVersion.Parse).Max();
        var version = requested is not null ? ReleaseVersion.Require(requested) :
            previousVersion is null ? new Version(5, 0, 0) : new Version(5, checked(previousVersion.Minor + 1), 0);
        var tag = $"v{version}";
        if (tags.Contains(tag, StringComparer.Ordinal)) throw new InvalidDataException($"标签 {tag} 已存在。");

        var temporary = Directory.CreateTempSubdirectory("orisumi-release-");
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(temporary.FullName, "release")).FullName;
            var release = await ReleaseBuild.Build(checkout, directory, "prod", version.ToString(), [], dotnet, token, host);
            if (release.Source != source || await ReleaseBuild.Source(checkout, token) != source)
                throw new InvalidDataException("构建期间工作树或 HEAD 发生变化，请重新运行 release。");

            Instance dev;
            try { dev = DevInstance.Open(devRoot); }
            catch (FileNotFoundException) { throw DevMismatch(release.Components.Keys.Order(StringComparer.Ordinal)); }
            var installed = ReleaseManifest.ReadInstalled(dev.Store);
            var differences = release.Components.Keys.Union(installed?.Components.Keys.AsEnumerable() ?? [], StringComparer.Ordinal)
                .Where(name => release.Components.GetValueOrDefault(name)?.Identity != installed?.Components.GetValueOrDefault(name)?.Identity)
                .Order(StringComparer.Ordinal).ToArray();
            if (differences.Length > 0) throw DevMismatch(differences);
            await WriteDevStatus(dev.Store, installed!, output, token, host);

            ReleaseManifest? previous = null;
            if (previousVersion is not null)
            {
                var name = ReleaseVersion.AssetPrefix(previousVersion) + ".json";
                var download = Directory.CreateDirectory(Path.Combine(temporary.FullName, "previous")).FullName;
                await Gh("release", "download", $"v{previousVersion}", "--pattern", name, "--dir", download);
                previous = ReleaseManifest.Read(Path.Combine(download, name));
                if (previous.Schema is null || previous.Components.Keys.Any(component => component != "host" && !previous.Schema.ContainsKey(component)))
                    throw new InvalidDataException($"发布 v{previousVersion} 的清单缺少采集器的数据库结构。");
                var violations = SchemaCompatibility.Violations(previous, release);
                if (violations.Count > 0)
                    throw new InvalidDataException("数据库结构有不兼容的改动：\n" + string.Join('\n', violations));
            }

            var prefix = ReleaseVersion.AssetPrefix(version);
            var package = Path.Combine(temporary.FullName, prefix + ".tar.gz");
            var manifest = Path.Combine(temporary.FullName, prefix + ".json");
            await Repository.Query("/usr/bin/tar", checkout, token, ["-czf", package, "-C", directory, "."]);
            (release with { Package = new(Path.GetFileName(package), ReleaseManifest.HashFile(package)) }).Write(manifest);

            var notes = string.Join('\n', release.Components.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => $"- {item.Key}: {(previous?.Components.GetValueOrDefault(item.Key)?.Identity == item.Value.Identity
                    ? "unchanged" : "changed")} ({item.Value.Version})"));
            output.Write(await Gh("release", "create", tag, "--target", source.Commit, "--title", tag, "--notes", notes, package, manifest));
        }
        finally { temporary.Delete(recursive: true); }

        Task<string> Git(params string[] arguments) => Repository.Query("git", checkout, token, arguments);
        Task<string> Gh(params string[] arguments) => Repository.Query("gh", checkout, token, arguments);
    }

    private static InvalidDataException DevMismatch(IEnumerable<string> components) =>
        new($"开发实例的组件标识不匹配：{string.Join(", ", components)}。请先运行 orisumi-dev deploy。");

    private static async Task WriteDevStatus(Store store, ReleaseManifest installed, TextWriter output,
        CancellationToken token, HostBundle.Tools host)
    {
        output.WriteLine($"dev installed_at_utc_ms: {installed.InstalledAtUtcMs?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
        try
        {
            var code = await host.Run(store.HostExecutable, ["--store", store.Root, "capture", "status"], output, token);
            token.ThrowIfCancellationRequested();
            if (code != 0) output.WriteLine($"dev capture status: exit {code}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            output.WriteLine($"dev capture status: {error.Message}");
        }
    }
}
