using System.CommandLine;
using System.Globalization;
using System.Reflection;
using Orisumi.Capturing;
using Orisumi.Raw;

namespace Orisumi.Cli;

public static class Program
{
    private static string Version { get; } =
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public static Task<int> Main(string[] args)
    {
        var root = new RootCommand("把人留下的痕迹采成原件块");
        var store = new Option<string>("--store")
        {
            Description = "原件存储目录",
            DefaultValueFactory = _ => RawStore.DefaultDirectory,
            Recursive = true,
        };
        root.Options.Add(store);
        root.Subcommands.Add(Capture(store));
        root.Subcommands.Add(RawCommands(store));
        var parse = root.Parse(args);
        return parse.InvokeAsync(new InvocationConfiguration
        {
            ProcessTerminationTimeout = TimeSpan.FromSeconds(10),
            Output = parse.CommandResult.Command.Name == "browser" ? Console.Error : Console.Out,
        });
    }

    private static Command Capture(Option<string> storeDirectory)
    {
        var directory = new Argument<string>("directory") { Description = "持续采集的来源目录" };
        var interval = new Option<int>("--interval") { Description = "每轮完成后等待的秒数", DefaultValueFactory = _ => 300 };
        var retries = new Option<int>("--retries") { Description = "文件读取失败后在本轮重试的次数", DefaultValueFactory = _ => 1 };
        var once = new Option<bool>("--once") { Description = "只采集一轮；部分完成或失败时返回非零状态" };
        var files = new Command("files", "立即采集目录中的文件，随后定期自动采集") { directory };
        files.SetAction((parse, token) => RunCapture(() =>
        {
            var path = Path.GetFullPath(parse.GetValue(directory)!);
            return new CaptureSource("files", path, [new FileRoot(path)]);
        }, parse.GetValue(storeDirectory)!, parse.GetValue(interval), parse.GetValue(retries), parse.GetValue(once), token));

        var projects = new Option<string>("--projects") { Description = "会话文件目录，默认 ~/.claude/projects" };
        var desktopSessions = new Option<string>("--desktop-sessions")
        {
            Description = "桌面会话索引目录，默认 ~/Library/Application Support/Claude/claude-code-sessions",
        };
        var claudeCode = new Command("claude-code", "立即采集 Claude Code 的会话文件和桌面索引，随后定期自动采集") { projects, desktopSessions };
        claudeCode.SetAction((parse, token) => RunCapture(
            () => ClaudeCodeSource.Create(parse.GetValue(projects), parse.GetValue(desktopSessions)),
            parse.GetValue(storeDirectory)!, parse.GetValue(interval), parse.GetValue(retries), parse.GetValue(once), token));
        var home = new Option<string>("--home") { Description = "Codex 数据目录，默认 ~/.codex" };
        var codex = new Command("codex", "采集 Codex 会话、附件、生成产物和完整状态库") { home };
        codex.SetAction((parse, token) => RunCapture(
            () => CodexSource.Create(parse.GetValue(home)),
            parse.GetValue(storeDirectory)!, parse.GetValue(interval), parse.GetValue(retries), parse.GetValue(once), token));
        foreach (var command in new[] { files, claudeCode, codex })
        {
            command.Options.Add(interval);
            command.Options.Add(retries);
            command.Options.Add(once);
        }
        return new Command("capture", "把一个来源采成原件块") { files, claudeCode, codex, Foreground(storeDirectory), Browser(storeDirectory) };
    }

    private static Command Browser(Option<string> storeDirectory)
    {
        var caller = new Argument<string>("caller-origin") { Description = "浏览器传入的扩展 origin" };
        var command = new Command("browser", "接收 Native Messaging 帧，保存浏览器观测后逐条回执") { caller };
        command.SetAction(async (parse, token) =>
        {
            try
            {
                using var input = Console.OpenStandardInput();
                using var output = Console.OpenStandardOutput();
                var receiver = new NativeReceiver(parse.GetValue(storeDirectory)!, $"orisumi capture browser {Version}");
                await receiver.RunAsync(input, output, parse.GetValue(caller)!, token);
                return 0;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return 130;
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        });
        return command;
    }

    private static Command Foreground(Option<string> storeDirectory)
    {
        var duration = new Option<int?>("--duration") { Description = "观察秒数，须大于零；省略时持续到手动停止" };
        var command = new Command("foreground", "在 macOS 逐条保存前台应用观测") { duration };
        command.SetAction(async (parse, token) =>
        {
            try
            {
                var seconds = parse.GetValue(duration);
                var collector = new ForegroundCollector(parse.GetValue(storeDirectory)!, $"orisumi capture foreground {Version}");
                var result = await collector.RunAsync(seconds is { } value ? TimeSpan.FromSeconds(value) : null, token);
                Console.WriteLine($"已保存 {result.Observations} 条观测");
                if (result.LastResult is { } last) Console.WriteLine(Summary(last));
                if (result.Failure is { } failure)
                {
                    Console.Error.WriteLine(failure);
                    return 1;
                }
                return token.IsCancellationRequested ? 130 : 0;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return 130;
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        });
        return command;
    }

    private static async Task<int> RunCapture(Func<CaptureSource> createSource, string storeDirectory, int interval, int retries, bool once, CancellationToken token)
    {
        try
        {
            var source = createSource();
            var collector = new Collector(storeDirectory, $"orisumi capture {source.Name} {Version}", retries);
            if (once)
            {
                var round = collector.Scan(source, token);
                PrintRound(round);
                return round.Failure is null ? 0 : 1;
            }
            await foreach (var round in collector.Watch(source, TimeSpan.FromSeconds(interval), token))
            {
                PrintRound(round);
            }
            return 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static Command RawCommands(Option<string> storeDirectory)
    {
        var raw = new Command("raw", "查看和管理原件采集记录");
        raw.Subcommands.Add(List(storeDirectory));
        raw.Subcommands.Add(Show(storeDirectory));
        raw.Subcommands.Add(Line(storeDirectory));
        raw.Subcommands.Add(Restore(storeDirectory));
        raw.Subcommands.Add(Export(storeDirectory));
        raw.Subcommands.Add(Delete(storeDirectory));
        return raw;
    }

    private static Command List(Option<string> storeDirectory)
    {
        var command = new Command("list", "按时间从早到晚列出仍保留的采集");
        command.SetAction(parse => Guard(() =>
        {
            var store = new RawStore(parse.GetValue(storeDirectory)!);
            foreach (var capture in store.Captures())
            {
                var record = capture.Record;
                var files = record.Manifest is null ? 0 : store.Manifest(capture).Count;
                var state = record.Failure is { } failure
                    ? $"{(record.Manifest is null ? "失败" : $"部分完成，{files} 项材料")}：{failure.Reason}"
                    : $"{files} 项材料";
                Console.WriteLine($"{capture.Name.Abbreviation} {record.Source} {Stamp(record.CapturedAt)} {record.Host} {state}");
            }
        }));
        return command;
    }

    private static Command Show(Option<string> storeDirectory)
    {
        var name = new Argument<string>("capture") { Description = "采集记录的完整名字或唯一前缀" };
        var command = new Command("show", "查看一份采集记录及其文件") { name };
        command.SetAction(parse => Guard(() =>
        {
            var store = new RawStore(parse.GetValue(storeDirectory)!);
            var capture = store.Find(parse.GetValue(name)!);
            var record = capture.Record;
            Console.WriteLine($"{capture.Name}");
            Console.WriteLine($"来源 {record.Source}，{Stamp(record.CapturedAt)} 在 {record.Host} 由 {record.Collector} 采自 {record.Origin}，切块器 {record.Chunker}");
            if (record.Failure is { } failure) Console.WriteLine($"失败：{failure.Reason}");
            foreach (var entry in store.Manifest(capture))
                Console.WriteLine($"{entry.Path}  {entry.Bytes.ToString(CultureInfo.InvariantCulture)} 字节，{entry.Blocks.Count} 块，取得方式 {entry.Method ?? "未记录"}{(entry.LinkTarget is null ? "" : $"，链接目标 {entry.LinkTarget}")}");
            if (record.CompletedAt is { } completedAt) Console.WriteLine($"采集完成于 {Stamp(completedAt)}；各份材料在这一时间窗口内分别取得");
        }));
        return command;
    }

    private static Command Line(Option<string> storeDirectory)
    {
        var position = new Argument<string>("position") { Description = "<来源>/<采集记录>/<路径>:<行号>，采集记录可用唯一前缀" };
        var command = new Command("line", "读取原件在指定位置的一行") { position };
        command.SetAction(parse => Guard(() =>
        {
            var store = new RawStore(parse.GetValue(storeDirectory)!);
            Console.WriteLine(store.Line(store.ParsePosition(parse.GetValue(position)!)));
        }));
        return command;
    }

    private static Command Delete(Option<string> storeDirectory)
    {
        var name = new Argument<string>("capture") { Description = "采集记录的完整名字或唯一前缀" };
        var reason = new Option<string>("--reason") { Description = "删除原因", Required = true };
        var command = new Command("delete", "为一份采集写入删除记录") { name, reason };
        command.SetAction(parse => Guard(() =>
        {
            var store = new RawStore(parse.GetValue(storeDirectory)!);
            var capture = store.Find(parse.GetValue(name)!);
            store.Delete(capture, parse.GetValue(reason)!);
            Console.WriteLine($"已为 {capture.Source}/{capture.Name.Abbreviation} 记录删除，原件字节尚未物理回收");
        }));
        return command;
    }

    private static int Guard(Action action, CancellationToken token = default)
    {
        try
        {
            action();
            return 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception error) when (error is FormatException or KeyNotFoundException or InvalidOperationException
            or IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static Command Restore(Option<string> storeDirectory)
    {
        var name = new Argument<string>("capture") { Description = "采集记录的完整名字或唯一前缀" };
        var directory = new Argument<string>("directory") { Description = "尚不存在的恢复目录" };
        var command = new Command("restore", "恢复一轮采集中的材料及原始清单，并核对内容") { name, directory };
        command.SetAction((parse, token) => Task.FromResult(Guard(() =>
        {
            var store = new RawStore(parse.GetValue(storeDirectory)!);
            var capture = store.Find(parse.GetValue(name)!);
            var result = new CaptureRecovery(store).Restore(capture, parse.GetValue(directory)!, token);
            Console.WriteLine($"已恢复到 {result.Directory}：{result.Files} 个文件、{result.Links} 个符号链接，{Megabytes(result.Bytes)} MB");
            if (capture.Record.Failure is { } failure) Console.WriteLine($"来源采集部分完成：{failure.Reason}");
        }, token)));
        return command;
    }

    private static Command Export(Option<string> storeDirectory)
    {
        var name = new Argument<string>("capture") { Description = "采集记录的完整名字或唯一前缀" };
        var path = new Argument<string>("path") { Description = "清单中的文件路径" };
        var destination = new Argument<string>("destination") { Description = "尚不存在的目标文件" };
        var command = new Command("export", "导出一个文件的原始字节，并核对内容") { name, path, destination };
        command.SetAction((parse, token) => Task.FromResult(Guard(() =>
        {
            var store = new RawStore(parse.GetValue(storeDirectory)!);
            var bytes = new CaptureRecovery(store).Export(store.Find(parse.GetValue(name)!), parse.GetValue(path)!, parse.GetValue(destination)!, token);
            Console.WriteLine($"已导出 {bytes.ToString(CultureInfo.InvariantCulture)} 字节到 {Path.GetFullPath(parse.GetValue(destination)!)}");
        }, token)));
        return command;
    }

    private static void PrintRound(CaptureRound round)
    {
        if (round.Result is { } result) Console.WriteLine(Summary(result));
        else Console.Error.WriteLine(round.StorageFailure);
    }

    private static string Summary(CaptureResult result)
    {
        var capture = result.Capture;
        var head = $"{capture.Source}/{capture.Name.Abbreviation}";
        var record = capture.Record;
        if (record.Manifest is null) return $"{head}：失败，{record.Failure!.Reason}";
        var summary = $"{head}：{result.Files} 项材料，{Megabytes(result.Bytes)} MB，新块 {result.NewBlocks} 个，{Megabytes(result.NewBytes)} MB";
        return record.Failure is { } failure ? $"{summary}，部分完成：{failure.Reason}" : summary;
    }

    private static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string Megabytes(long bytes) => (bytes / 1024.0 / 1024.0).ToString("0.#", CultureInfo.InvariantCulture);
}
