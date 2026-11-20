using System.CommandLine;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Orisumi.Platform;

namespace Orisumi.Dev;

internal static partial class Deploy
{
    private const string CollectorPrefix = "Orisumi.Collectors.";
    internal const string AppIconMaster = "src/Orisumi.App.MacOS/Icon/AppIcon.png";

    internal static void AddTo(RootCommand root, string dotnet)
    {
        var prod = new Option<string>("--prod")
        {
            Description = "生产实例的存储目录。",
            DefaultValueFactory = _ => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "Orisumi"),
        };
        var components = new Argument<string[]>("component") { Arity = ArgumentArity.ZeroOrMore };
        var deploy = new Command("deploy", "构建并安装当前提交的程序，再启动生产采集。") { prod, components };
        deploy.SetAction((result, token) => Run(Repository.Require(), result.GetRequiredValue(prod),
            result.GetRequiredValue(components), dotnet, token));
        root.Add(deploy);
    }

    private static async Task Run(string checkout, string prod, string[] components, string dotnet, CancellationToken token)
    {
        var head = await CleanHead(checkout, token);
        var selected = Select(checkout, components);
        var temporary = Directory.CreateTempSubdirectory("orisumi-deploy-");
        try
        {
            var directories = new List<string>();
            foreach (var (component, project) in selected)
            {
                var directory = Path.Combine(temporary.FullName, component);
                await Publish(checkout, directory, component, project, head, dotnet, token);
                if (await CleanHead(checkout, token) != head) throw new IOException("部署期间 HEAD 改变。");
                directories.Add(directory);
            }

            token.ThrowIfCancellationRequested();
            var store = Instance.Create(prod, "prod").Store;
            var host = HostBundle.Executable(Path.Combine(temporary.FullName, "host"));
            await RunHost("Install", host, ["--store", store.Root, "install", .. directories], token);
            await RunHost("Capture start", store.HostExecutable, ["--store", store.Root, "capture", "start"], token);
        }
        finally { temporary.Delete(recursive: true); }
    }

    private static (string Component, string Project)[] Select(string checkout, string[] components)
    {
        (string Component, string Project)[] all =
        [
            ("host", Repository.CliProject),
            .. Directory.GetDirectories(Path.Combine(checkout, "src/Collectors"), CollectorPrefix + "*")
                .Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)
                .Select(folder => (folder[CollectorPrefix.Length..].ToLowerInvariant(), $"src/Collectors/{folder}/{folder}.csproj")),
        ];
        if (components.Length == 0) return all;
        foreach (var component in components)
            if (!all.Any(item => item.Component == component)) throw new ArgumentException($"未知的部署组件：{component}");
        return all.Where(item => item.Component == "host" || components.Contains(item.Component)).ToArray();
    }

    private static async Task Publish(string checkout, string directory, string component, string project, string head,
        string dotnet, CancellationToken token)
    {
        var projectVersion = (await Query(dotnet, checkout, token, "msbuild", project, "-nologo", "-getProperty:Version")).Trim();
        var version = projectVersion + "+" + head;
        var publish = directory;
        if (component == "host")
        {
            var identities = await Query("/usr/bin/security", checkout, token, "find-identity", "-v", "-p", "codesigning");
            if (!identities.Split('\n').Any(line => line.TrimEnd().EndsWith($"\"{HostBundle.Authority}\"", StringComparison.Ordinal)))
                throw new IOException($"找不到代码签名身份 {HostBundle.Authority}，请检查钥匙串。");
            publish = Path.GetDirectoryName(HostBundle.Executable(directory))!;
        }

        using var build = new StringWriter();
        using var errors = new StringWriter();
        var code = await RunProcess.Run(dotnet,
            ["publish", project, "-c", "Release", "-r", "osx-arm64", "-p:PublishAot=true",
                $"-p:SourceRevisionId={head}", "-warnaserror", "-tl:off", "-o", publish],
            build, token, directory: checkout, errors: errors);
        var log = build.ToString() + errors;
        await Console.Out.WriteAsync(log);
        token.ThrowIfCancellationRequested();
        if (code != 0) throw new IOException($"{component}: AOT publish exited with code {code}");
        var warnings = Warning().Matches(log).Count;
        if (warnings != 0) throw new IOException($"{component}: AOT publish reported {warnings} warnings");

        if (component == "host")
        {
            await BuildApp(checkout, directory, token);
            await BuildAppIcon(checkout, directory, token);
            await SignHost(directory, publish, projectVersion, version, token);
        }
        if (component == "browser")
        {
            var extension = Path.Combine(checkout, DevCommand.ExtensionDirectory);
            if (await Development.Run("npm", extension, token, "ci") != 0 ||
                await ExtensionBuild.Build(checkout, Path.Combine(directory, "extension"), token) != 0)
                throw new IOException("browser: Chrome extension build failed");
        }
        await Verify(directory, component, version, token);
    }

    private static async Task Verify(string directory, string component, string version, CancellationToken token)
    {
        var executable = component == "host" ? HostBundle.Executable(directory) :
            Path.Combine(directory, ProgramDescription.ExecutableName(component));
        if (!File.Exists(executable)) throw new FileNotFoundException($"找不到发布程序：{executable}");
        if (component == "host") await HostBundle.Verify(directory, token);
        var description = await ProgramDescription.Read(executable, [], token);
        if (description.Name != component || description.Version != version)
            throw new InvalidDataException($"{directory}: expected {component} {version}, found {description.Name} {description.Version}");
    }

    private static async Task BuildApp(string checkout, string directory, CancellationToken token)
    {
        string[] arguments = ["build", "-c", "release", "--package-path", "src/Orisumi.App.MacOS",
            "--product", HostBundle.AppExecutableName, "--scratch-path", Path.Combine(Path.GetDirectoryName(directory)!, "swift-build")];
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var code = await RunProcess.Run("swift", [.. arguments, "-Xswiftc", "-warnings-as-errors"], output, token,
            directory: checkout, errors: errors);
        var log = output.ToString() + errors;
        await Console.Out.WriteAsync(log);
        token.ThrowIfCancellationRequested();
        if (code != 0 || Warning().IsMatch(log))
            throw new IOException($"host: Swift Release build failed (exit {code})");

        var bin = (await Query("swift", checkout, token, [.. arguments, "--show-bin-path"])).Trim();
        File.Copy(Path.Combine(bin, HostBundle.AppExecutableName), HostBundle.AppExecutable(directory));
    }

    private static async Task BuildAppIcon(string checkout, string directory, CancellationToken token)
    {
        var resources = Directory.CreateDirectory(Path.Combine(directory, HostBundle.Name, "Contents", "Resources"));
        var iconset = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(directory)!, "AppIcon.iconset"));
        var master = Path.Combine(checkout, AppIconMaster);
        foreach (var points in new[] { 16, 32, 128, 256, 512 })
        {
            foreach (var scale in new[] { 1, 2 })
            {
                var pixels = (points * scale).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var name = $"icon_{points}x{points}{(scale == 2 ? "@2x" : "")}.png";
                await Query("/usr/bin/sips", checkout, token, "-z", pixels, pixels, master,
                    "--out", Path.Combine(iconset.FullName, name));
            }
        }
        await Query("/usr/bin/iconutil", checkout, token, "-c", "icns", iconset.FullName,
            "-o", Path.Combine(resources.FullName, "AppIcon.icns"));
    }

    private static async Task SignHost(string directory, string publish, string projectVersion, string version, CancellationToken token)
    {
        var bundle = Path.Combine(directory, HostBundle.Name);
        var info = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"), new XElement("dict",
                new XElement("key", "CFBundleIdentifier"), new XElement("string", HostBundle.Identifier),
                new XElement("key", "CFBundleName"), new XElement("string", "Orisumi"),
                new XElement("key", "CFBundleDisplayName"), new XElement("string", "Orisumi"),
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
            var code = await RunProcess.Run(HostBundle.Codesign,
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

    private static async Task<string> CleanHead(string checkout, CancellationToken token)
    {
        var status = await Query("git", checkout, token, "status", "--porcelain=v1", "--untracked-files=all");
        if (status.Length != 0) throw new IOException("部署需要干净的工作树。");
        return (await Query("git", checkout, token, "rev-parse", "HEAD")).Trim();
    }

    private static async Task<string> Query(string executable, string directory, CancellationToken token, params string[] arguments)
    {
        using var output = new StringWriter();
        var code = await RunProcess.Run(executable, arguments, output, token, directory: directory);
        token.ThrowIfCancellationRequested();
        if (code != 0) throw new IOException($"{executable} {string.Join(' ', arguments)} exited with code {code}");
        return output.ToString();
    }

    [GeneratedRegex(@"(?im)\bwarning(?:\s+[^:\s]+)?\s*:|^\s*[1-9]\d* Warning\(s\)")]
    private static partial Regex Warning();

    private static async Task RunHost(string step, string executable, string[] arguments, CancellationToken token)
    {
        var code = await RunProcess.Run(executable, arguments, null, token);
        token.ThrowIfCancellationRequested();
        if (code != 0) throw new IOException($"{step}: {executable} exited with code {code}");
    }
}
