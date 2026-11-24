using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Xml.Linq;

namespace Orisumi.Platform;

[SupportedOSPlatform("macos")]
internal static class Launchd
{
    private const int NotLoaded = 113;
    internal static string Domain => $"gui/{getuid()}";
    internal static string Service(string label) => $"{Domain}/{label}";

    internal static XDocument Plist(Instance instance, XElement dictionary)
    {
        dictionary.Add(new XElement("key", "AssociatedBundleIdentifiers"),
            new XElement("array", new XElement("string", instance.BundleIdentifier)));
        return new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"), dictionary));
    }

    internal static IEnumerable<string> CaptureAgentPlists(Instance instance, SystemLocations locations) =>
        Directory.Exists(locations.Agents)
            ? Directory.EnumerateFiles(locations.Agents, instance.CaptureAgentPrefix + "*.plist") : [];

    internal static string CaptureAgentPlist(Instance instance, SystemLocations locations, string name) =>
        Path.Combine(locations.Agents, instance.CaptureAgentLabel(name) + ".plist");

    internal static async Task StopAgent(string plist, SystemLocations locations, CancellationToken token)
    {
        var service = Service(Path.GetFileNameWithoutExtension(plist));
        var before = await Print(locations, service, token);
        if (before is not null)
        {
            var identity = Pid(before) is int pid ? ProcessIdentity.Of(pid) : null;
            var bootout = await Run(locations, token, "bootout", service);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                while (true)
                {
                    var current = await Print(locations, service, deadline.Token);
                    if (current is null) break;
                    if (bootout.Code != 0) throw Error("bootout", service, bootout.Error);
                    await Task.Delay(100, deadline.Token);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new IOException($"{service} 未能在 30 秒内停止。");
            }
            if (identity is not null) await identity.Wait(token);
        }
        if (File.Exists(plist)) File.Delete(plist);
    }

    internal static async Task Restore(string path, SystemLocations locations, byte[]? registration, bool loaded, CancellationToken token)
    {
        await StopAgent(path, locations, token);
        if (registration is null) return;
        File.WriteAllBytes(path, registration);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        if (loaded) await Bootstrap(locations, path, token);
    }

    internal static async Task Bootstrap(SystemLocations locations, string plist, CancellationToken token)
    {
        var domain = Domain;
        var result = await Run(locations, token, "bootstrap", domain, plist);
        if (result.Code != 0) throw Error("bootstrap", $"{domain} {plist}", result.Error);
    }

    internal static async Task<string?> Print(SystemLocations locations, string service, CancellationToken token)
    {
        var result = await Run(locations, token, "print", service);
        if (result.Code == NotLoaded) return null;
        if (result.Code != 0) throw Error("print", service, result.Error);
        return result.Output;
    }

    internal static string? Field(string output, string name)
    {
        var prefix = name + " = ";
        return output.Split('\n').Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
    }

    internal static int? Pid(string output) => int.TryParse(Field(output, "pid"), CultureInfo.InvariantCulture, out var pid) ? pid : null;

    internal static IOException Error(string action, string service, string error) =>
        new($"launchctl {action} {service}: {error.Trim()}");

    internal static async Task<(int Code, string Output, string Error)> Run(SystemLocations locations,
        CancellationToken cancellation, params string[] arguments)
    {
        var result = await Process.RunAndCaptureTextAsync(locations.Launchctl, arguments, cancellation);
        if (result.ExitStatus.Canceled) throw new OperationCanceledException(cancellation);
        return (result.ExitStatus.ExitCode, result.StandardOutput, result.StandardError);
    }

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern uint getuid();
}
