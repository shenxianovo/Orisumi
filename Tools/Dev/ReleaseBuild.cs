using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Orisumi.Platform;

namespace Orisumi.Dev;

internal static partial class ReleaseBuild
{
    internal const string AppPackage = "Host/App.MacOS";
    internal const string AppIconMaster = AppPackage + "/Icon/AppIcon.png";

    // Skips components with an installed identity match and writes a release directory only when there is work.
    internal static async Task<ReleaseManifest> Build(string checkout, string destination, string instance, string? version,
        string[] components, string dotnet, CancellationToken token, HostBundle.Tools host, ReleaseManifest? installed = null)
    {
        var selected = Select(checkout, components);
        var source = await Source(checkout, token);
        var manifest = new ReleaseManifest(version, instance, ReleaseManifest.MacOSArm64, source,
            new Dictionary<string, ReleaseComponent>(StringComparer.Ordinal));
        var scratch = Directory.CreateTempSubdirectory("orisumi-build-");
        try
        {
            foreach (var (component, project) in selected)
            {
                var root = component == "host" ? "Host" : Path.GetDirectoryName(project)!;
                var directory = ReleaseManifest.ComponentDirectory(destination, component);
                var identity = await ComponentIdentity.Read(checkout, root, project, dotnet, token);
                if (installed?.Components.GetValueOrDefault(component)?.Identity == identity) continue;
                var informationalVersion = $"{version ?? "dev"}+{identity}";
                await Publish(checkout, directory, component, project, instance, informationalVersion, scratch.FullName,
                    dotnet, host, token);
                if (component != "host")
                {
                    var executable = Path.Combine(directory, ProgramDescription.ExecutableName(component));
                    var schema = await Repository.Query(executable, checkout, token, ["schema"]);
                    manifest.Schema.Add(component, JsonSerializer.Deserialize(schema, ReleaseJson.Default.SchemaTableArray)
                        ?? throw new InvalidDataException($"{component}: 缺少数据库结构。"));
                }
                manifest.Components.Add(component, new(identity, informationalVersion, ReleaseManifest.HashFiles(directory)));
            }
            if (manifest.Components.Count > 0) manifest.Write(Path.Combine(destination, ReleaseManifest.FileName));
            return manifest;
        }
        finally { scratch.Delete(recursive: true); }
    }

    private static (string Component, string Project)[] Select(string checkout, string[] components)
    {
        (string Component, string Project)[] all =
        [
            ("host", Repository.CliProject),
            .. Directory.GetDirectories(Path.Combine(checkout, Repository.CollectorsDirectory))
                .Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)
                .Select(folder => (folder.ToLowerInvariant(),
                    $"{Repository.CollectorsDirectory}/{folder}/{Repository.CollectorProjectPrefix}{folder}.csproj")),
        ];
        if (components.Length == 0) return all;
        foreach (var component in components)
            if (!all.Any(item => item.Component == component)) throw new ArgumentException($"未知的部署组件：{component}");
        return all.Where(item => item.Component == "host" || components.Contains(item.Component)).ToArray();
    }

    private static async Task Publish(string checkout, string directory, string component, string project, string instance,
        string version, string scratch, string dotnet, HostBundle.Tools host, CancellationToken token)
    {
        var publish = component == "host" ? Path.GetDirectoryName(HostBundle.Executable(directory))! : directory;

        using var build = new StringWriter();
        using var errors = new StringWriter();
        var code = await RunProcess.Run(dotnet,
            ["publish", project, "-c", "Release", "-r", ReleaseManifest.MacOSArm64, "-p:PublishAot=true",
                $"-p:InformationalVersion={version}", "-p:IncludeSourceRevisionInInformationalVersion=false", "-warnaserror", "-tl:off", "-o", publish],
            build, token, directory: checkout, errors: errors);
        var log = build.ToString() + errors;
        await Console.Out.WriteAsync(log);
        token.ThrowIfCancellationRequested();
        if (code != 0) throw new IOException($"{component}: AOT publish exited with code {code}");
        var warnings = Warning().Matches(log).Count;
        if (warnings != 0) throw new IOException($"{component}: AOT publish reported {warnings} warnings");

        if (component == "host")
        {
            var app = await BuildApp(checkout, directory, scratch, token);
            await BuildAppIcon(checkout, directory, scratch, app, instance, token);
            var projectVersion = (await Repository.Query(dotnet, checkout, token, ["msbuild", project, "-nologo", "-getProperty:Version"])).Trim();
            await SignHost(directory, publish, instance, projectVersion, version, host.Codesign, token);
        }
        if (component == "browser")
        {
            var extension = Path.Combine(checkout, DevCommand.ExtensionDirectory);
            if (await Development.Run("npm", extension, token, "ci") != 0 ||
                await ExtensionBuild.Build(checkout, Path.Combine(directory, "extension"), version, token) != 0)
                throw new IOException("browser: Chrome extension build failed");
        }
        await Verify(directory, component, instance, version, host, token);
    }

    private static async Task Verify(string directory, string component, string instance, string version, HostBundle.Tools host, CancellationToken token)
    {
        var executable = component == "host" ? HostBundle.Executable(directory) :
            Path.Combine(directory, ProgramDescription.ExecutableName(component));
        if (!File.Exists(executable)) throw new FileNotFoundException($"找不到发布程序：{executable}");
        if (component == "host") await HostBundle.Verify(directory, instance, token, host);
        var description = await ProgramDescription.Read(executable, [], token, component == "host" ? host.Run : RunProcess.RunCommand);
        if (description.Name != component || description.Version != version)
            throw new InvalidDataException($"{directory}: expected {component} {version}, found {description.Name} {description.Version}");
    }

    // Returns the built GUI outside the bundle, so it can run before the bundle is signed.
    private static async Task<string> BuildApp(string checkout, string directory, string scratch, CancellationToken token)
    {
        string[] arguments = ["build", "-c", "release", "--package-path", AppPackage,
            "--product", HostBundle.AppExecutableName, "--scratch-path", Path.Combine(scratch, "swift-build")];
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var code = await RunProcess.Run("swift", [.. arguments, "-Xswiftc", "-warnings-as-errors"], output, token,
            directory: checkout, errors: errors);
        var log = output.ToString() + errors;
        await Console.Out.WriteAsync(log);
        token.ThrowIfCancellationRequested();
        if (code != 0 || Warning().IsMatch(log))
            throw new IOException($"host: Swift Release build failed (exit {code})");

        var bin = (await Repository.Query("swift", checkout, token, [.. arguments, "--show-bin-path"])).Trim();
        var app = Path.Combine(bin, HostBundle.AppExecutableName);
        File.Copy(app, HostBundle.AppExecutable(directory));
        return app;
    }

    // The menu bar draws the plain mascot; the dev app's icon gets the GUI's DEV badge (DevBadge.swift).
    private static async Task BuildAppIcon(string checkout, string directory, string scratch, string app, string instance,
        CancellationToken token)
    {
        var resources = Directory.CreateDirectory(Path.Combine(directory, HostBundle.Name, "Contents", "Resources"));
        var iconset = Directory.CreateDirectory(Path.Combine(scratch, "AppIcon.iconset"));
        var master = Path.Combine(checkout, AppIconMaster);
        await Repository.Query("/usr/bin/sips", checkout, token, ["-z", "128", "128", master,
            "--out", Path.Combine(resources.FullName, "Mascot.png")]);
        if (instance != "prod")
        {
            var badged = Path.Combine(scratch, "AppIcon-dev.png");
            await Repository.Query(app, checkout, token, ["--badge-icon", master, badged]);
            master = badged;
        }
        foreach (var points in new[] { 16, 32, 128, 256, 512 })
        {
            foreach (var scale in new[] { 1, 2 })
            {
                var pixels = (points * scale).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var name = $"icon_{points}x{points}{(scale == 2 ? "@2x" : "")}.png";
                await Repository.Query("/usr/bin/sips", checkout, token, ["-z", pixels, pixels, master,
                    "--out", Path.Combine(iconset.FullName, name)]);
            }
        }
        await Repository.Query("/usr/bin/iconutil", checkout, token, ["-c", "icns", iconset.FullName,
            "-o", Path.Combine(resources.FullName, "AppIcon.icns")]);
    }

    private static async Task SignHost(string directory, string publish, string instance, string projectVersion, string version,
        string codesign, CancellationToken token)
    {
        var bundle = Path.Combine(directory, HostBundle.Name);
        var info = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"), new XElement("dict",
                new XElement("key", "CFBundleIdentifier"), new XElement("string", Instance.BundleIdentifierFor(instance)),
                new XElement("key", "CFBundleName"), new XElement("string", Instance.AppNameFor(instance)),
                new XElement("key", "CFBundleDisplayName"), new XElement("string", Instance.AppNameFor(instance)),
                new XElement("key", "CFBundleExecutable"), new XElement("string", HostBundle.AppExecutableName),
                new XElement("key", "CFBundleIconFile"), new XElement("string", "AppIcon.icns"),
                new XElement("key", "CFBundlePackageType"), new XElement("string", "APPL"),
                new XElement("key", "CFBundleShortVersionString"), new XElement("string", projectVersion),
                new XElement("key", "CFBundleVersion"), new XElement("string", version),
                new XElement("key", "LSMinimumSystemVersion"), new XElement("string", "14.0"),
                new XElement("key", "LSUIElement"), new XElement("true"))));

        foreach (var symbols in Directory.GetDirectories(publish, "*.dSYM", SearchOption.AllDirectories))
            Directory.Delete(symbols, recursive: true);
        foreach (var file in Directory.EnumerateFiles(publish, "*", SearchOption.AllDirectories))
        {
            if (Path.GetExtension(file) == ".pdb") File.Delete(file);
            else if (IsMachO(file)) await Sign(file);
        }
        info.Save(Path.Combine(bundle, "Contents", "Info.plist"));
        await Sign(bundle);

        async Task Sign(string path)
        {
            using var errors = new StringWriter();
            var code = await RunProcess.Run(codesign,
                ["--force", "--sign", HostBundle.Authority, "--timestamp=none", path], TextWriter.Null, token, errors: errors);
            token.ThrowIfCancellationRequested();
            if (code != 0) throw new IOException($"{path}: {errors.ToString().Trim()}");
        }
    }

    private static bool IsMachO(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length < sizeof(uint)) return false;
        using var header = new BinaryReader(file);
        return header.ReadUInt32() is 0xfeedface or 0xfeedfacf or 0xcefaedfe or 0xcffaedfe
            or 0xcafebabe or 0xbebafeca or 0xcafebabf or 0xbfbafeca;
    }

    internal static async Task<ReleaseSource> Source(string checkout, CancellationToken token)
    {
        var commit = (await Git("rev-parse", "HEAD")).Trim();
        var branch = (await Git("branch", "--show-current")).Trim();
        var clean = (await Git("--no-optional-locks", "status", "--porcelain=v1", "--untracked-files=all")).Length == 0;
        return new(commit, Path.GetFullPath(checkout), branch.Length == 0 ? null : branch, clean);

        Task<string> Git(params string[] arguments) => Repository.Query("git", checkout, token, arguments);
    }

    [GeneratedRegex(@"(?im)\bwarning(?:\s+[^:\s]+)?\s*:|^\s*[1-9]\d* Warning\(s\)")]
    private static partial Regex Warning();

}
