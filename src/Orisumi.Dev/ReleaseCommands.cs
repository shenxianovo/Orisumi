using System.CommandLine;
using Orisumi.Platform;

namespace Orisumi.Dev;

internal static class ReleaseCommands
{
    internal static void AddTo(RootCommand root)
    {
        var support = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");
        var releases = RootOption("--releases", "Orisumi-releases");
        var prod = RootOption("--prod", "Orisumi");
        var preview = RootOption("--preview", "Orisumi-preview");
        root.Add(releases);
        root.Add(prod);
        root.Add(preview);

        var releaseComponents = Components();
        var release = new Command("release", "发布当前提交的 Native AOT 程序。") { releaseComponents };
        release.SetAction((result, token) => Releases.Build(Repository.Require(), Path.GetFullPath(result.GetRequiredValue(releases)),
            result.GetRequiredValue(releaseComponents), Console.Out, token));
        root.Add(release);

        var previewComponents = Components();
        var start = new Command("start", "安装当前提交的发布版本并启动预览采集。") { previewComponents };
        start.SetAction((result, token) => InstanceWorkflow.Preview(Repository.Require(), Path.GetFullPath(result.GetRequiredValue(releases)),
            new Store(result.GetRequiredValue(preview)), result.GetRequiredValue(previewComponents), token));
        var stop = new Command("stop", "停止预览采集。");
        stop.SetAction((result, token) => InstanceWorkflow.Stop(new Store(result.GetRequiredValue(preview)), "preview", token));
        var status = new Command("status", "查看生产和预览的版本、最近运行与读取失败。");
        status.SetAction(async (result, token) =>
        {
            foreach (var (name, option) in new[] { ("prod", prod), ("preview", preview) })
            {
                var store = new Store(result.GetRequiredValue(option));
                var host = store.HostExecutable;
                if (!File.Exists(host))
                {
                    Console.WriteLine($"{name}: not installed ({store.Root})");
                    continue;
                }
                var code = await RunProcess.Run(host, ["capture", "status", "--store", store.Root], Console.Out, token);
                token.ThrowIfCancellationRequested();
                if (code != 0) throw new IOException($"Capture status: {host} exited with code {code}");
            }
        });
        root.Add(new Command("preview", "管理预览实例。") { start, stop, status });

        var promoteComponents = Components();
        var migrate = new Option<string?>("--migrate") { Description = "在安装前运行一次 C# 数据迁移。" };
        var promote = new Command("promote", "将预览版本安装到生产并切换采集。") { promoteComponents, migrate };
        promote.SetAction((result, token) => InstanceWorkflow.Promote(Repository.Require(), Path.GetFullPath(result.GetRequiredValue(releases)),
            new Store(result.GetRequiredValue(prod)), new Store(result.GetRequiredValue(preview)),
            result.GetRequiredValue(promoteComponents), result.GetValue(migrate), token));
        root.Add(promote);

        Option<string> RootOption(string name, string folder) => new(name)
        {
            Recursive = true,
            DefaultValueFactory = _ => Path.Combine(support, folder),
        };
    }

    private static Argument<string[]> Components() => new("component") { Arity = ArgumentArity.ZeroOrMore };
}
