using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Orisumi.Platform;

namespace Orisumi.Cli.Capture;

internal static class Launchd
{
    private const int NotLoaded = 113;
    internal static string Domain => $"gui/{getuid()}";
    internal static string Service(string label) => $"{Domain}/{label}";

    internal static XDocument Plist(XElement dictionary)
    {
        dictionary.Add(new XElement("key", "AssociatedBundleIdentifiers"),
            new XElement("array", new XElement("string", HostBundle.Identifier)));
        return new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"), dictionary));
    }

    internal static IEnumerable<string> CaptureAgentPlists(Instance instance, SystemLocations locations) =>
        Directory.Exists(locations.Agents)
            ? Directory.EnumerateFiles(locations.Agents, instance.CaptureAgentPrefix + "*.plist") : [];

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
