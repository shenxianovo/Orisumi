using System.CommandLine;
using System.Globalization;
using Orisumi.Readings;

namespace Orisumi.Context;

internal sealed class ContextCommands(Store store, TextWriter output)
{
    private static readonly Option<string> From = new("--from") { Description = "Start in local time, e.g. 2026-10-30 or 2026-10-30T18:00" };
    private static readonly Option<string> To = new("--to") { Description = "End in local time, exclusive" };

    public Command Create()
    {
        var context = new Command("context", "What an Agent reads about the person. Every line ends with a position in the raw captures.");
        context.Subcommands.Add(Sources());
        context.Subcommands.Add(Timeline());
        context.Subcommands.Add(Said());
        context.Subcommands.Add(Around());
        context.Subcommands.Add(Raw());
        return context;
    }

    private Command Sources()
    {
        var command = new Command("sources", "Captured sources, which devices and hours each covers, and recorded data loss");
        command.SetAction(_ =>
        {
            output.WriteLine($"时间是 {Clock.ZoneName}。");
            output.WriteLine();
            output.WriteLine("## 采集");
            foreach (var capture in store.Captures())
            {
                output.WriteLine($"- {capture.Source}：{Clock.ShowMinute(capture.From)} 到 {Clock.ShowMinute(capture.To)}，{capture.Files.Count} 个文件，"
                    + $"{capture.CapturedAt:yyyy-MM-dd HH:mm}Z 在 {capture.Host} 由 {capture.Collector} 采自 {Rendering.Cut(capture.Origin, 120)}"
                    + (capture.SourceVersion is { } version ? $"，来源版本 {version}" : ""));
            }
            output.WriteLine();
            output.WriteLine("## 覆盖");
            var (_, covers) = store.LatestReading<Cover>(CoverageReading.Name);
            foreach (var group in covers.GroupBy(cover => (cover.Device, cover.Kind)))
            {
                output.WriteLine($"### {group.Key.Device} · {group.Key.Kind}");
                foreach (var cover in group)
                    output.WriteLine($"- {Clock.ShowMinute(cover.Start)} 到 {Clock.ShowMinute(cover.End)} {cover.Note}  [{cover.Position}]");
            }
        });
        return command;
    }

    private Command Timeline()
    {
        var device = new Option<string>("--device") { Description = "Only this device, by the name shown" };
        var kind = new Option<string>("--kind") { Description = "Only this kind: 前台, 浏览器, VRChat, 键鼠, Claude Code 会话, Codex 会话, 提交" };
        var minimum = new Option<int>("--min-seconds") { Description = "Hide spans shorter than this, except 提交", DefaultValueFactory = _ => 60 };
        var command = new Command("timeline", "Spans on every device in time order: foreground app, browser tab, VRChat world, input per 10 minutes, assistant sessions, commits")
        {
            From, To, device, kind, minimum,
        };
        command.SetAction(parse =>
        {
            var range = Range(parse);
            var (_, spans) = store.LatestReading<TimelineEntry>(TimelineReading.Name);
            var shown = spans.Where(span => range.Overlaps(span.Start, span.End) || range.Contains(span.Start))
                .Where(span => parse.GetValue(device) is not { } name || span.Device == name)
                .Where(span => parse.GetValue(kind) is not { } wanted || span.Kind == wanted)
                .ToList();
            var visible = shown.Where(span => span.Kind == "提交" || (span.End - span.Start).TotalSeconds >= parse.GetValue(minimum)).ToList();
            output.WriteLine($"时间是 {Clock.ZoneName}。{shown.Count - visible.Count} 条短于 {parse.GetValue(minimum)} 秒的没有显示。");
            foreach (var span in visible)
                output.WriteLine($"{Clock.Show(span.Start)}–{Clock.Show(span.End)[6..]} {Duration(span)} [{span.Device} · {span.Kind}] {span.Label}  [{span.Position}]");
        });
        return command;
    }

    private Command Said()
    {
        var grep = new Option<string>("--grep") { Description = "Only utterances containing this text, case-insensitive" };
        var command = new Command("said", "What the person typed to coding assistants, in time order, with injected context left out") { From, To, grep };
        command.SetAction(parse =>
        {
            var range = Range(parse);
            var (_, utterances) = store.LatestReading<Utterance>(SaidReading.Name);
            output.WriteLine($"时间是 {Clock.ZoneName}。");
            foreach (var utterance in utterances.Where(utterance => range.Contains(utterance.At)))
            {
                if (parse.GetValue(grep) is { } text && !utterance.Text.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
                output.WriteLine($"--- {Clock.Show(utterance.At)} {utterance.Source} 「{utterance.Title}」 {utterance.Cwd}  [{utterance.Position}]");
                output.WriteLine(utterance.Text);
            }
        });
        return command;
    }

    private Command Around()
    {
        var position = new Argument<string>("position") { Description = "A position printed by another command" };
        var before = new Option<int>("--before") { Description = "Records to show before it", DefaultValueFactory = _ => 10 };
        var after = new Option<int>("--after") { Description = "Records to show after it", DefaultValueFactory = _ => 20 };
        var command = new Command("around", "Records next to a position in its raw file, rendered as text: what the person said, assistant replies, tool calls")
        {
            position, before, after,
        };
        command.SetAction(parse =>
        {
            var at = Position.Parse(parse.GetValue(position)!);
            var lines = File.ReadAllLines(store.PathOf(at));
            var rendered = new List<(int Line, string Text)>();
            for (var index = 0; index < lines.Length; index++)
            {
                if (lines[index].Length == 0) continue;
                if (Rendering.Render(at.Source, lines[index]) is { } text) rendered.Add((index + 1, text));
            }
            var center = rendered.FindIndex(entry => entry.Line >= at.Line);
            if (center < 0) center = rendered.Count - 1;
            var first = Math.Max(0, center - parse.GetValue(before));
            var last = Math.Min(rendered.Count - 1, center + parse.GetValue(after));
            output.WriteLine($"{at.Source}/{at.CaptureId}/{at.File}，时间是 {Clock.ZoneName}。");
            for (var index = first; index <= last; index++)
                output.WriteLine($"{(rendered[index].Line == at.Line ? ">" : " ")}{rendered[index].Line.ToString(CultureInfo.InvariantCulture)}: {rendered[index].Text}");
        });
        return command;
    }

    private Command Raw()
    {
        var position = new Argument<string>("position") { Description = "A position printed by another command" };
        var command = new Command("raw", "The raw line at a position, exactly as captured") { position };
        command.SetAction(parse =>
        {
            var at = Position.Parse(parse.GetValue(position)!);
            output.WriteLine(File.ReadLines(store.PathOf(at)).ElementAt(at.Line - 1));
        });
        return command;
    }

    private static TimeRange Range(ParseResult parse) => new(
        parse.GetValue(From) is { } from ? Clock.ParseLocal(from) : DateTimeOffset.MinValue,
        parse.GetValue(To) is { } to ? Clock.ParseLocal(to) : DateTimeOffset.MaxValue);

    private static string Duration(TimelineEntry span)
    {
        var length = span.End - span.Start;
        return length.TotalHours >= 1 ? $"{(int)length.TotalHours}h{length.Minutes:00}m" : length.TotalMinutes >= 1 ? $"{(int)length.TotalMinutes}m" : $"{(int)length.TotalSeconds}s";
    }
}
