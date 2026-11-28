using Orisumi.Platform;

namespace Orisumi.Dev;

internal sealed class WindowsHostBuilder : IHostBuilder
{
    public CommandProcess Run => RunProcess.RunCommand;
    public Task Complete(string checkout, string directory, string instance, string projectVersion, string version,
        CancellationToken token) => ReleaseBuild.PublishAot(checkout, directory, "supervisor", Repository.SupervisorProject,
            version, Platforms.CurrentRid, RunProcess.Dotnet, token);
}
