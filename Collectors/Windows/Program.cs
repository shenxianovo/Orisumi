using System.CommandLine;
using Orisumi.Capture.Hosting;
using Orisumi.Collectors.Windows;
using Orisumi.Core;

var store = CollectorCommand.StoreOption();
var root = CollectorCommand.Create(WindowsForeground.Collector, "Orisumi · Windows 采集", store, ["foreground", "--windows", "--input"]);
var seconds = new Argument<int?>("seconds") { Description = "采集秒数，省略时持续运行。", Arity = ArgumentArity.ZeroOrOne };
var windows = new Option<bool>("--windows") { Description = "采集窗口焦点和标题。" };
var input = new Option<bool>("--input") { Description = "采集按键和鼠标按钮。" };
var threshold = new Option<int>("--afk-threshold") { Description = "进入离开状态前的无输入秒数。", DefaultValueFactory = _ => 300 };
var foreground = new Command("foreground", "监听 Windows 前台活动。") { seconds, windows, input, threshold };
foreground.SetAction((result, cancellation) =>
{
    var duration = result.GetValue(seconds);
    var idle = result.GetValue(threshold);
    if (duration is <= 0 || idle <= 0) throw new ArgumentException("seconds 和 --afk-threshold 必须是正整数。");
    return WindowsForeground.Run(result.GetValue(store) ?? Instance.DefaultRoot(), duration, result.GetValue(windows), result.GetValue(input), idle, cancellation);
});
root.Add(foreground);
return await CollectorCommand.Run(root, args);
