using System.CommandLine;
using Orisumi.Desktop;
using System.Text.Json;
using Orisumi.Platform;

namespace Orisumi.Cli;

internal static class UpdateCommand
{
    internal static Command Create(Option<string?> store, DesktopServices desktop, TextWriter output, string? baseDirectory)
    {
        var version = new Argument<string?>("version") { Arity = ArgumentArity.ZeroOrOne };
        var from = new Option<string?>("--from") { Description = "从本地目录读取发布文件。" };
        var command = new Command("update", "将生产实例更新到指定版本或最新版本。") { version, from };
        command.SetAction((result, token) =>
        {
            var instance = Instance.Open(result.GetValue(store) ?? Instance.Resolve(baseDirectory ?? AppContext.BaseDirectory).Store.Root);
            return Run(instance, result.GetValue(version), result.GetValue(from), desktop.Jobs(instance), desktop.Installation(instance), output, token);
        });
        return command;
    }

    internal static async Task Run(Instance instance, string? requested, string? from, ICollectorJobs jobs,
        IDesktopInstallation platform, TextWriter output, CancellationToken token)
    {
        if (instance.Name != "prod") throw new ArgumentException("update 只用于 prod 实例。请用 orisumi-dev deploy 更新开发实例。");
        var store = instance.Store;
        var requestedVersion = requested is null ? null : ReleaseVersion.Require(requested);
        using var client = from is null ? new HttpClient() : null;
        if (client is not null) client.DefaultRequestHeaders.UserAgent.ParseAdd("Orisumi");
        var version = from is null ? await GitHubVersion(client!, requestedVersion, token) :
            requestedVersion ?? LocalVersion(from);

        var temporary = Directory.CreateTempSubdirectory("orisumi-update-");
        try
        {
            var prefix = ReleaseVersion.AssetPrefix(version, Platforms.CurrentRid);
            var manifestPath = await GetAsset(prefix + ".json");
            var release = ReleaseManifest.Read(manifestPath);
            release.VerifyFor(instance, Platforms.CurrentRid);
            if (release.Version is null || ReleaseVersion.Parse(release.Version) != version || !release.Components.ContainsKey("host"))
                throw new InvalidDataException($"发布清单与版本 {version} 不符，或缺少宿主组件。");

            if (IsCurrent(release, ReleaseManifest.ReadInstalled(store))) return;

            var packageName = prefix + ".tar.gz";
            if (release.Package is not { } package || package.Name != packageName ||
                package.Sha256 is not { Length: 64 } || !package.Sha256.All(char.IsAsciiHexDigit))
                throw new InvalidDataException("发布清单缺少有效的安装包名称或 SHA-256。");
            var archive = await GetAsset(packageName);
            if (!ReleaseManifest.HashFile(archive).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{packageName}: 安装包 SHA-256 不匹配。");

            var extracted = Directory.CreateDirectory(Path.Combine(temporary.FullName, "release")).FullName;
            await platform.ExtractPackage(archive, extracted, token);
            var packaged = ReleaseManifest.Read(Path.Combine(extracted, ReleaseManifest.FileName));
            if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(packaged, ReleaseJson.Default.ReleaseManifest),
                    JsonSerializer.SerializeToElement(release with { Package = null }, ReleaseJson.Default.ReleaseManifest)))
                throw new InvalidDataException("安装包中的清单与发布清单不符。");
            await platform.VerifyHost(ReleaseManifest.ComponentDirectory(extracted, "host"), requireReleaseCertificate: true, token);

            using var guard = Store.WaitLock(store.AppLock);
            var installed = ReleaseManifest.ReadInstalled(store);
            if (IsCurrent(release, installed)) return;
            var previous = installed?.Version ?? "pre-v5";
            if (installed?.Version is { } installedVersion) ReleaseVersion.Require(installedVersion);
            Backup(store, previous, platform, token);
            await InstallCommand.RunLocked(instance, extracted, jobs, platform, output, token);
            output.WriteLine($"Updated {previous} -> {version}.");
        }
        catch (HttpRequestException error)
        {
            throw new IOException($"无法下载发布文件：{error.Message}", error);
        }
        finally { temporary.Delete(recursive: true); }

        bool IsCurrent(ReleaseManifest release, ReleaseManifest? installed)
        {
            if (!release.HasSameComponents(installed)) return false;
            output.WriteLine($"Orisumi is up to date ({installed?.Version ?? version.ToString()}).");
            return true;
        }

        async Task<string> GetAsset(string name)
        {
            var path = Path.Combine(temporary.FullName, name);
            using var stream = from is not null ? File.OpenRead(Path.Combine(from, name)) :
                await client!.GetStreamAsync($"https://github.com/shenxianovo/Orisumi/releases/download/v{version}/{name}", token);
            await using var file = File.Create(path);
            await stream.CopyToAsync(file, token);
            return path;
        }
    }

    private static async Task<Version> GitHubVersion(HttpClient client, Version? requested, CancellationToken token)
    {
        var endpoint = requested is null ? "latest" : $"tags/v{requested}";
        try
        {
            using var stream = await client.GetStreamAsync($"https://api.github.com/repos/shenxianovo/Orisumi/releases/{endpoint}", token);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            if (!json.RootElement.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String ||
                ReleaseVersion.Parse(tag.GetString()!) is not { } version || requested is not null && version != requested)
                throw new InvalidDataException("GitHub Release 缺少有效的版本标签。");
            return version;
        }
        catch (HttpRequestException error)
        {
            throw new IOException($"无法读取 GitHub Release：{error.Message}", error);
        }
    }

    private static Version LocalVersion(string directory)
    {
        const string prefix = "orisumi-";
        var suffix = "-" + Platforms.CurrentRid + ".json";
        return Directory.EnumerateFiles(directory, prefix + "*" + suffix)
            .Select(path => Path.GetFileName(path)[prefix.Length..^suffix.Length])
            .Select(ReleaseVersion.Parse).Max() ??
            throw new FileNotFoundException($"找不到 {Platforms.CurrentRid} 发布清单：{directory}");
    }

    private static void Backup(Store store, string version, IDesktopInstallation platform, CancellationToken token)
    {
        var root = DirectoryPaths.Resolve(store.Root);
        if (DirectoryPaths.Resolve(store.Backups) != Path.Combine(root, "backups"))
            throw new InvalidDataException($"备份目录是符号链接：{store.Backups}");
        var staging = new Store(Path.Combine(store.Backups, ".backup-" + Guid.NewGuid().ToString("N")));
        try
        {
            Directory.CreateDirectory(staging.ObservationsRoot);
            StoreExport.Backup(store, staging, token);
            token.ThrowIfCancellationRequested();
            Directory.SetLastWriteTimeUtc(staging.Root, DateTime.UtcNow);
            var destination = Path.Combine(store.Backups, version);
            if (new DirectoryInfo(destination).LinkTarget is not null)
                throw new InvalidDataException($"备份目录是符号链接：{destination}");
            platform.ActivateDirectory(staging.Root, destination);

            foreach (var old in new DirectoryInfo(store.Backups).EnumerateDirectories()
                .Where(directory => directory.LinkTarget is null && (directory.Name == "pre-v5" || ReleaseVersion.Parse(directory.Name) is not null))
                .OrderByDescending(directory => directory.FullName == destination)
                .ThenByDescending(directory => directory.LastWriteTimeUtc).ThenByDescending(directory => directory.Name, StringComparer.Ordinal).Skip(2))
                old.Delete(recursive: true);
        }
        finally
        {
            if (Directory.Exists(staging.Root)) Directory.Delete(staging.Root, recursive: true);
        }
    }
}
