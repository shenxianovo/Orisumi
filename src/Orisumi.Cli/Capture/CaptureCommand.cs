using System.CommandLine;
using Orisumi.CommandLine;
using Orisumi.Platform;

namespace Orisumi.Cli.Capture;

internal static class CaptureCommand
{
    internal static Command Create(Option<string?> store)
    {
        var start = new Command("start", "启动或重启本机采集器，保留已采集的数据。");
        start.SetAction((result, token) => CaptureStartup.Start(Instance.Open(result.GetValue(store) ?? Instance.DefaultRoot()),
            SystemLocations.Current, Console.Out, token));
        var stop = new Command("stop", "停止这个实例的采集。");
        stop.SetAction((result, token) => CaptureStartup.Stop(Instance.Open(result.GetValue(store) ?? Instance.DefaultRoot()),
            SystemLocations.Current, Console.Out, token));
        var from = TimestampOption.Create("--from", "查询起点（含），必须带 Z 或 UTC 偏移。");
        var to = TimestampOption.Create("--to", "查询终点（不含），必须带 Z 或 UTC 偏移。");
        var coverage = new Command("coverage", "查询各来源的观测、失败与中断证据。") { from, to };
        coverage.SetAction(async (result, token) =>
        {
            var location = result.GetValue(store) ?? Instance.DefaultRoot();
            var programs = await CollectorPrograms.Read(location, token);
            CaptureCoverage.Write(location, result.GetValue(from), result.GetValue(to),
                programs.Select(program => program.Description.Name), Console.Out);
        });
        return new Command("capture", "管理本机采集器。") { start, stop, coverage };
    }
}
