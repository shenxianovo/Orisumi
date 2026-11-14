using System.CommandLine;
using Orisumi.Collectors.Network;

namespace Orisumi.Cli;

internal static class NetworkCommand
{
    internal static Command Create(Option<string> store)
    {
        var seconds = new Argument<int?>("seconds")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "采集秒数；省略时持续运行。",
        };
        var interval = new Option<int>("--interval")
        {
            DefaultValueFactory = _ => 5,
            Description = "nettop 采样间隔（秒），默认 5。",
        };
        var capture = new Command("capture", "定期采集 macOS 进程连接和收发计数。") { seconds, interval };
        capture.SetAction(result => NetworkCapture.Run(result.GetRequiredValue(store), result.GetValue(seconds),
            result.GetValue(interval), Console.Error));
        var from = Commands.TimestampOption("--from", "查询起点（含），必须带 Z 或 UTC 偏移。");
        var to = Commands.TimestampOption("--to", "查询终点（不含），必须带 Z 或 UTC 偏移。");
        var details = new Option<bool>("--details") { Description = "包含原始观测引用、连接出现/消失与分时计数。" };
        var query = new Command("query", "按时间查询网络观测，输出 JSON。") { from, to, details };
        query.SetAction(result => NetworkQuery.Write(result.GetRequiredValue(store), result.GetValue(from),
            result.GetValue(to), result.GetValue(details), Console.Out));
        return new Command("network", "设备网络观测。") { capture, query };
    }
}
