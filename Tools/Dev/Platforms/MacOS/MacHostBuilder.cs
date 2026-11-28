using System.Text.RegularExpressions;
using System.Xml.Linq;
using Orisumi.Platform;

namespace Orisumi.Dev;

internal sealed partial class MacHostBuilder(HostBundle.Tools tools) : IHostBuilder
{
    internal const string AppPackage = "Host/App.MacOS";
    internal const string AppIconMaster = AppPackage + "/Icon/AppIcon.png";

    public CommandProcess Run => tools.Run;

    public async Task Complete(string checkout, string directory, string instance, string projectVersion, string version,
        CancellationToken token)
    {
        var scratch = Directory.CreateTempSubdirectory("orisumi-build-");
        try
        {
            var app = await BuildApp(checkout, directory, scratch.FullName, token);
            await BuildAppIcon(checkout, directory, scratch.FullName, app, instance, token);
            var publish = Path.GetDirectoryName(HostBundle.Executable(directory))!;
            await SignHost(directory, publish, instance, projectVersion, version, tools.Codesign, token);
            await HostBundle.Verify(directory, instance, token, tools);
        }
        finally { scratch.Delete(recursive: true); }
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

    [GeneratedRegex(@"(?im)\bwarning(?:\s+[^:\s]+)?\s*:|^\s*[1-9]\d* Warning\(s\)")]
    private static partial Regex Warning();
}
