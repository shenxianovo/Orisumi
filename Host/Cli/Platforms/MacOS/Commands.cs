using System.CommandLine;
using Orisumi.Desktop;
using Orisumi.Platform;

namespace Orisumi.Cli;

internal static partial class Commands
{
    private static partial DesktopServices? CurrentDesktop() => new(
        instance => new MacCollectorJobs(instance, SystemLocations.Current),
        instance => new MacInstallation(instance, SystemLocations.Current, HostBundle.Tools.Current));

    internal static partial Task<int?> Forward(RootCommand root, string[] arguments) =>
        arguments is [LaunchCommand.Name, .. var child] ? Launch(child) : CollectorPrograms.TryForward(root, arguments);

    private static async Task<int?> Launch(string[] arguments) => await LaunchCommand.Run(arguments);
}
