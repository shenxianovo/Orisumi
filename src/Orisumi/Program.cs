using System.CommandLine;
using Orisumi.Capturing;
using Orisumi.Context;
using Orisumi.Readings;

namespace Orisumi;

public static class Program
{
    public static Task<int> Main(string[] args)
    {
        var store = Store.Open();
        var root = new RootCommand("Orisumi: capture what a person leaves behind, read it, and hand it to Agents as context");
        root.Subcommands.Add(Capture(store));
        root.Subcommands.Add(Read(store));
        root.Subcommands.Add(new ContextCommands(store, Console.Out).Create());
        return root.Parse(args).InvokeAsync();
    }

    private static Command Capture(Store store)
    {
        var capture = new Command("capture", "Save one source as raw captures");
        var directory = new Option<string>("--dir") { Description = "Directory holding projects/ and desktop-sessions/", DefaultValueFactory = _ => ".local/benchmark/claude-code" };
        capture.Subcommands.Add(Source("claude-code", "Claude Code session logs and desktop session index, copied file by file",
            parse => Task.FromResult(ClaudeCodeCapture.Run(store, parse.GetValue(directory)!, TimeRange.Of(parse))), directory));
        var codex = new Option<string>("--home") { Description = "Codex home", DefaultValueFactory = _ => Home(".codex") };
        capture.Subcommands.Add(Source("codex", "Codex rollouts touching the range, the title index, and thread rows",
            parse => CodexCapture.RunAsync(store, parse.GetValue(codex)!, TimeRange.Of(parse)), codex));
        var host = new Option<string>("--host") { Description = "SSH host running the old Heartbeat database", DefaultValueFactory = _ => "heartbeat-maintenance" };
        capture.Subcommands.Add(Source("heartbeat", "Rows of the old Heartbeat database: segments, input events, losses, devices and apps",
            parse => HeartbeatCapture.RunAsync(store, parse.GetValue(host)!, TimeRange.Of(parse)), host));
        var code = new Option<string>("--code") { Description = "Directory searched three levels deep for repositories", DefaultValueFactory = _ => Home("Code") };
        capture.Subcommands.Add(Source("git", "Raw commit objects and their numstat for commits in the range",
            parse => GitCapture.RunAsync(store, parse.GetValue(code)!, TimeRange.Of(parse)), code));
        return capture;
    }

    private static Command Source(string name, string description, Func<ParseResult, Task<Capture>> run, Option<string> location)
    {
        var command = new Command(name, description) { TimeRange.FromOption, TimeRange.ToOption, location };
        command.SetAction(async parse =>
        {
            var capture = await run(parse);
            Console.WriteLine($"{capture.Source}/{capture.Id}: {capture.Files.Count} files, {capture.Files.Sum(file => file.Bytes) / 1024 / 1024} MB");
        });
        return command;
    }

    private static Command Read(Store store)
    {
        var command = new Command("read", "Read the latest captures into said, timeline and coverage");
        command.SetAction(_ =>
        {
            var heartbeat = store.Latest("heartbeat");
            var claude = store.Latest("claude-code");
            var codex = store.Latest("codex");
            var git = store.Latest("git");
            var said = store.WriteReading(SaidReading.Name, [claude, codex], SaidReading.Read(store, claude, codex));
            var spans = TimelineReading.Read(store, heartbeat, claude, codex, git).ToList();
            var timeline = store.WriteReading(TimelineReading.Name, [heartbeat, claude, codex, git], spans);
            var coverage = store.WriteReading(CoverageReading.Name, [heartbeat, claude, codex, git], CoverageReading.Read(store, heartbeat, spans));
            foreach (var reading in new[] { said, timeline, coverage }) Console.WriteLine($"{reading.Name}/{reading.Id}");
        });
        return command;
    }

    private static string Home(string folder) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), folder);
}
