using System.CommandLine;
using Orisumi.Desktop;

namespace Orisumi.Cli;

internal static partial class Commands
{
    private static partial DesktopServices? CurrentDesktop() => new(
        instance => new WindowsCollectorJobs(instance), instance => new WindowsInstallation(instance));

    internal static partial Task<int?> Forward(RootCommand root, string[] arguments) =>
        CollectorPrograms.TryForward(root, arguments);
}
