using System.CommandLine;

namespace Orisumi.Cli;

internal static partial class Commands
{
    private static partial DesktopServices? CurrentDesktop() => null;
    internal static partial Task<int?> Forward(RootCommand root, string[] arguments) => Task.FromResult<int?>(null);
}
