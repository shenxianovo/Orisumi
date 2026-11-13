using System.CommandLine;
using System.Globalization;
using System.Text.RegularExpressions;
using Orisumi.Collectors.Browser;
using Orisumi.Collectors.Claude;
using Orisumi.Collectors.Codex;
using Orisumi.Collectors.MacOS;
using Spectre.Console;

namespace Orisumi.Cli;

internal static class Commands
{
    internal static RootCommand Create()
    {
        var store = new Option<string>("--store")
        {
            Description = "采集存储目录；各来源数据库放在 raw/ 下。",
            DefaultValueFactory = _ => ".orisumi",
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
        var afkThreshold = new Option<int?>("--afk-threshold") { Description = "进入离开状态前的无输入秒数。" };
        var foreground = new Command("foreground", "监听 macOS 前台活动。") { seconds, windows, input, afkThreshold };
        foreground.SetAction(result =>
        {
            var duration = result.GetValue(seconds);
            var threshold = result.GetValue(afkThreshold);
            if (duration is <= 0 || threshold is <= 0)
                throw new ArgumentException("seconds 和 --afk-threshold 必须是正整数。");
            return MacOSForeground.Run(result.GetRequiredValue(store), duration,
                result.GetValue(windows), threshold, result.GetValue(input));
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

        var threadId = new Argument<string?>("thread-id")
        {
            Description = "Codex 会话 ID；省略时读取 CODEX_THREAD_ID。",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var all = new Option<bool>("--all") { Description = "采集所有 Codex 会话。" };
        var codexCapture = new Command("capture", "导入 Codex 会话的当前可见内容与变化。") { threadId, all };
        codexCapture.SetAction(result =>
        {
            var id = result.GetValue(threadId);
            if (result.GetValue(all) && id is not null)
                throw new ArgumentException("thread-id 和 --all 只能选择一个。");
            id = result.GetValue(all) ? null : id ?? Environment.GetEnvironmentVariable("CODEX_THREAD_ID");
            if (!result.GetValue(all) && string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("请提供 thread-id 或 --all，或设置 CODEX_THREAD_ID。");
            AnsiConsole.Write(new Text(CodexCollector.Capture(result.GetRequiredValue(store), id) + Environment.NewLine));
        });
        root.Add(new Command("codex", "Codex 会话采集。") { codexCapture });

        var claudeCapture = new Command("capture", "导入 Claude Code 会话的当前可见内容与变化。");
        claudeCapture.SetAction(result =>
            AnsiConsole.Write(new Text(ClaudeCollector.Capture(result.GetRequiredValue(store)) + Environment.NewLine)));
        root.Add(new Command("claude", "Claude Code 会话采集。") { claudeCapture });

        return root;
    }

    private static Option<DateTimeOffset> TimestampOption(string name, string description) => new(name)
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
