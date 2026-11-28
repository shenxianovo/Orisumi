using Orisumi.Core;
using Orisumi.Desktop;

namespace Orisumi.Dev;

internal sealed class WindowsHostBuilder : IHostBuilder
{
    public CommandProcess Run => RunProcess.RunCommand;
    public async Task Complete(string checkout, string directory, string instance, string projectVersion, string version,
        CancellationToken token)
    {
        await ReleaseBuild.PublishAot(checkout, directory, "supervisor", Repository.SupervisorProject,
            version, Platforms.CurrentRid, RunProcess.Dotnet, token);
        await ReleaseBuild.PublishAot(checkout, directory, "app", Repository.AppProject,
            version, Platforms.CurrentRid, RunProcess.Dotnet, token);
        foreach (var file in new[] { WindowsInstallation.AppExecutable, "AppIcon.png" })
        {
            var path = Path.Combine(directory, file);
            if (!File.Exists(path)) throw new FileNotFoundException($"缺少托盘发布文件：{path}", path);
        }
    }
}
