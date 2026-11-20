using System.Text.RegularExpressions;
using System.Xml.Linq;
using Orisumi.Platform;

namespace Orisumi.Dev;

internal static partial class Releases
{
    private const string CollectorPrefix = "Orisumi.Collectors.";

    // The host plus every collector project; a new collector needs no entry here.
    private static string[] All(string checkout) =>
        ["host", .. CollectorProjects(checkout).Select(project => project.Component)];

    private static IEnumerable<(string Component, string Project)> CollectorProjects(string checkout) =>
        Directory.GetDirectories(Path.Combine(checkout, "src/Collectors"), CollectorPrefix + "*")
            .Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)
            .Select(folder => (folder[CollectorPrefix.Length..].ToLowerInvariant(), $"src/Collectors/{folder}/{folder}.csproj"));

    internal static string[] Select(string checkout, string[] components, bool includeHost = false)
    {
        var all = All(checkout);
        if (components.Length == 0) return all;
        foreach (var component in components)
            if (!all.Contains(component)) throw new ArgumentException($"未知的发布组件：{component}");
        return all.Where(component => (includeHost && component == "host") || components.Contains(component)).ToArray();
    }

    internal static async Task Build(string checkout, string root, string[] components, TextWriter output, CancellationToken token)
    {
        var head = await CleanHead(checkout, token);
        foreach (var component in Select(checkout, components))
        {
            var (version, warnings) = await Publish(checkout, root, component, head, output, token,
                async () =>
                {
                    if (await CleanHead(checkout, token) != head) throw new IOException("发布期间 HEAD 改变。");
                });
            await output.WriteLineAsync($"{component} {version} ({warnings} warnings)");
        }
    }

    internal static async Task<(string Version, int Warnings)> Publish(string checkout, string root, string component, string head,
        TextWriter output, CancellationToken token, Func<Task> verifyCheckout)
    {
        var project = component == "host" ? Repository.CliProject
            : CollectorProjects(checkout).Single(collector => collector.Component == component).Project;
        var projectVersion = (await Query(RunProcess.Dotnet, checkout, token, "msbuild", project, "-nologo", "-getProperty:Version")).Trim();
        var version = projectVersion + "+" + head;
        var parent = Path.Combine(root, component);
        var target = Path.Combine(parent, version);
        if (Directory.Exists(target))
        {
            await Verify(target, component, version, token);
            return (version, 0);
        }

        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, "." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var publish = temporary;
            if (component == "host")
            {
                var identities = await Query("/usr/bin/security", checkout, token, "find-identity", "-v", "-p", "codesigning");
                if (!identities.Split('\n').Any(line => line.TrimEnd().EndsWith($"\"{HostBundle.Authority}\"", StringComparison.Ordinal)))
                    throw new IOException($"找不到代码签名身份 {HostBundle.Authority}，请检查钥匙串。");
                publish = Path.GetDirectoryName(HostBundle.Executable(temporary))!;
            }

            using var build = new StringWriter();
            using var errors = new StringWriter();
            var code = await RunProcess.Run(RunProcess.Dotnet,
                ["publish", project, "-c", "Release", "-r", "osx-arm64", "-p:PublishAot=true",
                    $"-p:SourceRevisionId={head}", "-warnaserror", "-tl:off", "-o", publish],
                build, token, directory: checkout, errors: errors);
            var log = build.ToString() + errors;
            await output.WriteAsync(log);
            token.ThrowIfCancellationRequested();
            if (code != 0) throw new IOException($"{component}: AOT publish exited with code {code}");
            var warnings = Warning().Matches(log).Count;
            if (warnings != 0) throw new IOException($"{component}: AOT publish reported {warnings} warnings");

            if (component == "host")
                await SignHost(temporary, publish, projectVersion, version, token);
            if (component == "browser")
            {
                var directory = Path.Combine(checkout, DevCommand.ExtensionDirectory);
                if (await Development.Run("npm", directory, token, "ci") != 0 ||
                    await ExtensionBuild.Build(checkout, Path.Combine(temporary, "extension"), token) != 0)
                    throw new IOException("browser: Chrome extension build failed");
            }
            await Verify(temporary, component, version, token);
            await verifyCheckout();
            try { Directory.Move(temporary, target); }
            catch (IOException) when (Directory.Exists(target)) { await Verify(target, component, version, token); }
            return (version, warnings);
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }

    internal static async Task Verify(string directory, string component, string version, CancellationToken token)
    {
        var executable = component == "host" ? HostBundle.Executable(directory) :
            Path.Combine(directory, ProgramDescription.ExecutableName(component));
        if (!File.Exists(executable)) throw new FileNotFoundException($"找不到发布程序：{executable}");
        if (component == "host") await HostBundle.Verify(directory, token);
        var description = await ProgramDescription.Read(executable, [], token);
        if (description.Name != component || description.Version != version)
            throw new InvalidDataException($"{directory}: expected {component} {version}, found {description.Name} {description.Version}");
    }

    private static async Task SignHost(string release, string publish, string projectVersion, string version, CancellationToken token)
    {
        var bundle = Path.Combine(release, HostBundle.Name);
        var info = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"), new XElement("dict",
                new XElement("key", "CFBundleIdentifier"), new XElement("string", HostBundle.Identifier),
                new XElement("key", "CFBundleName"), new XElement("string", "Orisumi"),
                new XElement("key", "CFBundleExecutable"), new XElement("string", ProgramDescription.ExecutableName("host")),
                new XElement("key", "CFBundlePackageType"), new XElement("string", "APPL"),
                new XElement("key", "CFBundleShortVersionString"), new XElement("string", projectVersion),
                new XElement("key", "CFBundleVersion"), new XElement("string", version),
                new XElement("key", "LSUIElement"), new XElement("true"))));
        info.Save(Path.Combine(bundle, "Contents", "Info.plist"));

        foreach (var symbols in Directory.GetDirectories(publish, "*.dSYM", SearchOption.AllDirectories))
            Directory.Delete(symbols, recursive: true);
        foreach (var file in Directory.EnumerateFiles(publish, "*", SearchOption.AllDirectories))
        {
            if (Path.GetExtension(file) == ".pdb") File.Delete(file);
            else if (IsMachO(file)) await Sign(file);
        }
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

    internal static async Task<string> Head(string checkout, CancellationToken token) =>
        (await Query("git", checkout, token, "rev-parse", "HEAD")).Trim();

    internal static string ForHead(string root, string component, string head)
    {
        var parent = Path.Combine(root, component);
        var releases = Directory.Exists(parent)
            ? Directory.GetDirectories(parent, "*+" + head).Where(path => !Path.GetFileName(path).StartsWith('.')).ToArray()
            : [];
        return releases switch
        {
            [var path] => path,
            [] => throw new DirectoryNotFoundException($"{component}: HEAD {head} 尚无发布版本（{parent}）。"),
            _ => throw new InvalidDataException($"{component}: HEAD {head} 对应多个发布版本（{parent}）。"),
        };
    }

    private static async Task<string> CleanHead(string checkout, CancellationToken token)
    {
        var status = await Query("git", checkout, token, "status", "--porcelain=v1", "--untracked-files=all");
        if (status.Length != 0) throw new IOException("发布需要干净的工作树。");
        return await Head(checkout, token);
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
}
