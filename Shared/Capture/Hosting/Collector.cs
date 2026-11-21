using System.Reflection;

namespace Orisumi.Capture.Hosting;

// The collector program the shared capture code runs for: its source name, version and Schema/ resources.
public sealed class Collector(string source, Assembly assembly)
{
    public string Source { get; } = source;
    public Assembly Assembly { get; } = assembly;
    public string Version { get; } = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
}
