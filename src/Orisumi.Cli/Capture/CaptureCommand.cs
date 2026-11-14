using System.CommandLine;

namespace Orisumi.Cli.Capture;

internal static class CaptureCommand
{
    internal static Command Create(Option<string> store)
    {
        var extension = new Option<string?>("--extension-id")
        {
            Description = "同时注册 Chrome 扩展的本机接收端；注册后在 Chrome 中重载扩展。",
        };
        var start = new Command("start", "启动或重启本机采集器，保留已采集的数据。") { extension };
        start.SetAction((result, token) => CaptureStartup.Start(result.GetRequiredValue(store), result.GetValue(extension), Console.Out, token));
        var from = Commands.TimestampOption("--from", "查询起点（含），必须带 Z 或 UTC 偏移。");
        var to = Commands.TimestampOption("--to", "查询终点（不含），必须带 Z 或 UTC 偏移。");
        var coverage = new Command("coverage", "查询各来源的观测、失败与中断证据。") { from, to };
        coverage.SetAction(result => CaptureCoverage.Write(result.GetRequiredValue(store), result.GetValue(from), result.GetValue(to), Console.Out));
        return new Command("capture", "管理本机采集器。") { start, coverage };
    }
}
