using System.CommandLine;
using System.Globalization;
using System.Text.RegularExpressions;
using Orisumi.Collectors.Browser;
using Orisumi.Collectors.Claude;
using Orisumi.Collectors.Codex;
using Orisumi.Collectors.MacOS;
using Orisumi.Platform;
using Spectre.Console;

namespace Orisumi.Cli;

internal static class Commands
{
    internal static RootCommand Create()
    {
        var store = new Option<string>("--store")
        {
            Description = "采集存储目录，默认 ~/Library/Application Support/Orisumi；布局见文档「多设备」。",
            DefaultValueFactory = _ => Store.DefaultRoot(),
            Recursive = true,
        };
        var root = new RootCommand("Orisumi · 采集、查询与开发") { store };
        root.HelpName = "orisumi";

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
            return MacOSForeground.Run(result.GetRequiredValue(store), duration,
                result.GetValue(windows), threshold, result.GetValue(input),
                result.GetValue(microphone), result.GetValue(audioOutput), result.GetValue(camera));
        });
        root.Add(foreground);

        var from = TimestampOption("--from", "查询起点（含），必须带 Z 或 UTC 偏移。");
        var to = TimestampOption("--to", "查询终点（不含），必须带 Z 或 UTC 偏移。");
        var timeline = new Command("timeline", "查询指定时间段的活动时间线。") { from, to };
        timeline.SetAction(result =>
        {
            if (result.GetValue(from) >= result.GetValue(to))
                throw new ArgumentException("--from 必须早于 --to。");
            return ActivityTimeline.Write(result.GetRequiredValue(store), result.GetValue(from), result.GetValue(to), Console.Out);
        });
        root.Add(timeline);
        root.Add(Capture.CaptureCommand.Create(store));

        // Chrome uses a binary protocol on stdout. Keep this action free of terminal rendering.
        var nativeHost = new Command("native-host", "接收 Chrome Native Messaging 消息。") { Hidden = true };
        nativeHost.SetAction(result => ChromeNativeHost.Run(result.GetRequiredValue(store)));
        var extensionId = new Argument<string>("extension-id") { Description = "Chrome 扩展页显示的扩展 ID。" };
        var register = new Command("register-chrome", "注册当前版本的 Chrome 本机接收端。") { extensionId };
        register.SetAction(result =>
        {
            var executable = Path.Combine(AppContext.BaseDirectory, "Orisumi");
            AnsiConsole.Write(new Text(ChromeNativeHost.Register(result.GetRequiredValue(store),
                result.GetRequiredValue(extensionId), executable) + Environment.NewLine));
        });
        root.Add(new Command("browser", "配置浏览器采集。") { nativeHost, register });
        root.Add(Dev.DevCommand.Create());

        // Resident collectors: capture start runs them as LaunchAgents; each takes the source's single writer.
        var codexCapture = new Command("capture", "持续监听 Codex 会话并增量导入。");
        codexCapture.SetAction((result, token) =>
            Capture.CaptureLifetime.Run(stop => CodexCollector.Watch(result.GetRequiredValue(store), stop), token));
        root.Add(new Command("codex", "Codex 会话采集。") { codexCapture });

        var claudeCapture = new Command("capture", "持续监听 Claude Code 会话日志并增量导入。");
        claudeCapture.SetAction((result, token) =>
            Capture.CaptureLifetime.Run(stop => ClaudeCollector.Watch(result.GetRequiredValue(store), stop), token));
        root.Add(new Command("claude", "Claude Code 会话采集。") { claudeCapture });

        return root;
    }

    internal static Option<DateTimeOffset> TimestampOption(string name, string description) => new(name)
    {
        Description = description,
        Required = true,
        CustomParser = result =>
        {
            var value = result.Tokens.Single().Value;
            if (Regex.IsMatch(value, @"(?:Z|[+-]\d{2}:\d{2})\z") &&
                DateTimeOffset.TryParseExact(value,
                    ["yyyy-MM-dd'T'HH:mmK", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"],
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
                return timestamp;
            result.AddError($"{name} 必须是带 Z 或 UTC 偏移的 ISO 8601 时间。");
            return default;
        },
    };
}
