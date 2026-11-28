using System.CommandLine;
using Orisumi.Desktop;
using Orisumi.Core;

namespace Orisumi.Dev;

internal static class Deploy
{
    internal static Command Create(string rid, Func<Instance, ICollectorJobs> jobs, IHostBuilder host)
    {
        var store = DevInstance.StoreOption();
        var components = new Argument<string[]>("component") { Arity = ArgumentArity.ZeroOrMore };
        var deploy = new Command("deploy", "构建工作树，安装到开发实例并启动采集。") { store, components };
        deploy.SetAction((result, token) => Run(Repository.Require(), result.GetRequiredValue(store), result.GetRequiredValue(components),
            rid, RunProcess.Dotnet, jobs, host, Console.Out, token));
        return deploy;
    }

    internal static async Task Run(string checkout, string root, string[] components, string rid, string dotnet,
        Func<Instance, ICollectorJobs> jobs, IHostBuilder host, TextWriter output, CancellationToken token)
    {
        var instance = DevInstance.Open(root, create: true);
        var store = instance.Store;
        var temporary = Directory.CreateTempSubdirectory("orisumi-deploy-");
        try
        {
            // Only components that differ from what the dev instance runs are built; the rest stay as installed.
            var installed = ReleaseManifest.ReadInstalled(store);
            var release = await ReleaseBuild.Build(checkout, temporary.FullName, instance.Name, null, components, rid, dotnet, token, host, installed);
            if (release.Components.Count > 0)
            {
                var newBrowser = release.Components.ContainsKey("browser") && installed?.Components.ContainsKey("browser") != true;
                var built = Store.HostExecutableIn(ReleaseManifest.ComponentDirectory(temporary.FullName, "host"));
                await RunHost(File.Exists(built) ? built : store.HostExecutable, "install", temporary.FullName);
                if (newBrowser) ChromeExtension.PrintLocation(instance, output);
            }
            else
            {
                using var guard = Store.WaitLock(store.AppLock);
                var current = ReleaseManifest.ReadInstalled(store);
                if (current is null || !current.HasSameComponents(installed))
                    throw new IOException("构建期间已安装的组件发生变化，请重新运行 deploy。");
                if (current.Source != release.Source)
                {
                    var manifest = store.Manifest + "." + Guid.NewGuid().ToString("N");
                    try
                    {
                        (current with { Source = release.Source }).Write(manifest);
                        AtomicFile.Replace(manifest, store.Manifest);
                    }
                    finally { File.Delete(manifest); }
                }
                output.WriteLine("No component changed.");
            }
            if (!(await jobs(instance).Inspect([], token)).Values.Any(job => job.Supervised))
                await RunHost(store.HostExecutable, "capture", "start");
        }
        finally { temporary.Delete(recursive: true); }

        async Task RunHost(string executable, params string[] arguments)
        {
            var code = await host.Run(executable, ["--store", store.Root, .. arguments], output, token);
            token.ThrowIfCancellationRequested();
            if (code != 0) throw new IOException($"{executable} {arguments[0]} 退出码 {code}。");
        }
    }
}
