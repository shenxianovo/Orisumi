using System.CommandLine;
using Orisumi.Collectors.Hosting;
using Orisumi.Collectors.MacOS;
using Orisumi.CommandLine;
using Orisumi.Platform;

var store = CollectorCommand.StoreOption();
var root = CollectorCommand.Create("macos", "Orisumi · macOS 采集", store,
    ["foreground", "--windows", "--input", "--microphone", "--audio-output", "--camera"]);
var seconds = new Argument<int?>("seconds")
{
    Description = "采集秒数；省略时持续运行，按 Ctrl+C 停止。",
    Arity = ArgumentArity.ZeroOrOne,
};
var windows = new Option<bool>("--windows") { Description = "采集窗口焦点和标题。" };
var input = new Option<bool>("--input") { Description = "采集键盘和鼠标事件。" };
var microphone = new Option<bool>("--microphone") { Description = "采集各进程的麦克风占用状态。" };
var audioOutput = new Option<bool>("--audio-output") { Description = "采集各进程的音频输出状态。" };
var camera = new Option<bool>("--camera") { Description = "采集摄像头设备的占用状态。" };
var afkThreshold = new Option<int?>("--afk-threshold") { Description = "进入离开状态前的无输入秒数。" };
var foreground = new Command("foreground", "监听 macOS 前台活动。")
    { seconds, windows, input, afkThreshold, microphone, audioOutput, camera };
foreground.SetAction(result =>
{
    var duration = result.GetValue(seconds);
    var threshold = result.GetValue(afkThreshold);
    if (duration is <= 0 || threshold is <= 0)
        throw new ArgumentException("seconds 和 --afk-threshold 必须是正整数。");
    return MacOSForeground.Run(result.GetValue(store) ?? Instance.DefaultRoot(), duration,
        result.GetValue(windows), threshold, result.GetValue(input),
        result.GetValue(microphone), result.GetValue(audioOutput), result.GetValue(camera));
});
root.Add(foreground);

var from = TimestampOption.Create("--from", "查询起点（含），必须带 Z 或 UTC 偏移。");
var to = TimestampOption.Create("--to", "查询终点（不含），必须带 Z 或 UTC 偏移。");
var timeline = new Command("timeline", "查询指定时间段的活动时间线。") { from, to };
timeline.SetAction(result =>
{
    if (result.GetValue(from) >= result.GetValue(to))
        throw new ArgumentException("--from 必须早于 --to。");
    return ActivityTimeline.Write(result.GetValue(store) ?? Instance.DefaultRoot(), result.GetValue(from), result.GetValue(to), Console.Out);
});
root.Add(timeline);
return await CollectorCommand.Run(root, args);
