using System.CommandLine;
using Orisumi.Platform;

namespace Orisumi.Cli.Capture;

internal static class CaptureCommand
{
    internal static Command Create(Option<string?> store, DesktopServices? desktop, TextWriter output, string? baseDirectory)
    {
        var from = TimestampOption.Create("--from", "查询起点（含），必须带 Z 或 UTC 偏移。");
        var to = TimestampOption.Create("--to", "查询终点（不含），必须带 Z 或 UTC 偏移。");
        var coverage = new Command("coverage", "查询各来源的观测、失败与中断证据。") { from, to };
        coverage.SetAction(async (result, token) =>
        {
            var location = result.GetValue(store) ?? Instance.DefaultRoot();
            var programs = await CollectorPrograms.Read(location, token, baseDirectory);
            CaptureCoverage.Write(location, result.GetValue(from), result.GetValue(to),
                programs.Select(program => program.Description.Name), output);
        });
        var capture = new Command("capture", "管理本机采集器。");
        if (desktop is not null) AddManagement(capture, store, desktop, output, baseDirectory);
        capture.Add(coverage);
        return capture;
    }

    private static void AddManagement(Command capture, Option<string?> store, DesktopServices desktop, TextWriter output, string? baseDirectory)
    {
        var start = new Command("start", "启动或重启本机采集器，保留已采集的数据。");
        start.SetAction((result, token) =>
        {
            var instance = Instance.Open(result.GetValue(store) ?? Instance.DefaultRoot());
            return CaptureStartup.Start(instance, desktop.Jobs(instance), output, token, baseDirectory);
        });
        var stop = new Command("stop", "停止这个实例的采集。");
        stop.SetAction((result, token) =>
        {
            var instance = Instance.Open(result.GetValue(store) ?? Instance.DefaultRoot());
            return CaptureStartup.Stop(instance, desktop.Jobs(instance), output, token);
        });
        var json = new Option<bool>("--json") { Description = "以 JSON 输出采集状态。" };
        var status = new Command("status", "查看本机采集器的进程与最近运行。") { json };
        status.SetAction((result, token) =>
        {
            var instance = result.GetValue(store) is { } location ? Instance.Open(location)
                : Instance.Resolve(baseDirectory ?? AppContext.BaseDirectory);
            return CaptureStatus.Write(instance, desktop.Jobs(instance), result.GetValue(json), output, token, baseDirectory);
        });
        capture.Add(start);
        capture.Add(stop);
        capture.Add(status);
    }
}
