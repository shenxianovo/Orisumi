using System.CommandLine;
using Orisumi.Desktop;
using Orisumi.Core;

namespace Orisumi.Dev;

internal static class DevInstance
{
    internal static Option<string> StoreOption() => new("--store")
    {
        Description = "开发实例的存储目录。",
        DefaultValueFactory = _ => Instance.DevelopmentRoot(),
    };

    internal static Instance Open(string root, bool create = false)
    {
        if (string.Equals(DirectoryPaths.Resolve(root), Path.GetFullPath(Instance.ProductionRoot()), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("此命令只用于 dev 实例。");
        var instance = create ? Instance.Create(root, "dev") : Instance.Open(root);
        if (instance.Name != "dev") throw new ArgumentException($"此命令只用于 dev 实例，当前为 {instance.Name}。");
        return instance;
    }

    internal static Task<int> Forward(string[] arguments, CommandProcess? run = null)
    {
        var root = Instance.DevelopmentRoot();
        for (var i = 0; i < arguments.Length && arguments[i] != "--"; i++)
        {
            var argument = arguments[i];
            if (argument == "--store" && i + 1 < arguments.Length)
                root = OpenForForwarding(arguments[++i]).Store.Root;
            else if (argument.StartsWith("--store=", StringComparison.Ordinal) || argument.StartsWith("--store:", StringComparison.Ordinal))
                root = OpenForForwarding(argument[8..]).Store.Root;
        }
        var host = OpenForForwarding(root).Store.HostExecutable;
        if (!File.Exists(host)) throw MissingHost();
        return RunProcess.WithCancellation(token => run is null
            ? RunProcess.Run(host, arguments, null, token, preserveExitCode: true)
            : run(host, arguments, null, token));
    }

    private static IOException MissingHost() => new("开发实例未安装宿主，请运行 orisumi-dev deploy。");

    private static Instance OpenForForwarding(string root)
    {
        try { return Open(root); }
        catch (FileNotFoundException) { throw MissingHost(); }
    }

    internal static Command ResetCommand(Func<Instance, ICollectorJobs> jobs)
    {
        var store = StoreOption();
        var command = new Command("reset", "停止开发采集并删除观测、运行状态和日志。") { store };
        command.SetAction((result, token) => Reset(result.GetRequiredValue(store), jobs, token));
        return command;
    }

    internal static async Task Reset(string root, Func<Instance, ICollectorJobs> jobs, CancellationToken token)
    {
        var instance = Open(root);
        var store = instance.Store;
        using var guard = Store.WaitLock(store.AppLock);
        await jobs(instance).StopAll(token);
        foreach (var directory in new[] { store.ObservationsRoot, store.State, store.Logs })
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
