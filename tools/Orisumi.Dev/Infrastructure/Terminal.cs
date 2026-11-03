using System.Diagnostics;
using System.Globalization;
using Spectre.Console;

namespace Orisumi.Dev;

internal enum Outcome
{
    Passed,
    Failed,
    Cancelled,
    Skipped,
    Running,
    Attention,
    Waiting,
}

internal static class Terminal
{
    public const string Dot = " [grey]·[/] ";

    public static IAnsiConsole Create(TextWriter writer)
    {
        var terminal = IsTerminal(writer);
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(terminal ? writer : new TrailingSpaceTrimmer(writer)),
            Ansi = terminal ? AnsiSupport.Detect : AnsiSupport.No,
            ColorSystem = terminal && !NoColor ? ColorSystemSupport.Detect : ColorSystemSupport.NoColors,
            Interactive = terminal ? InteractionSupport.Detect : InteractionSupport.No,
        });
        if (!terminal) console.Profile.Width = 240;
        return console;
    }

    private static bool NoColor => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

    private static bool IsTerminal(TextWriter writer) =>
        Environment.GetEnvironmentVariable("TERM") != "dumb"
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))
        && (ReferenceEquals(writer, Console.Out) ? !Console.IsOutputRedirected
            : ReferenceEquals(writer, Console.Error) && !Console.IsErrorRedirected);

    public static string Esc(string text) => Markup.Escape(text);

    public static string Mark(Outcome outcome) => outcome switch
    {
        Outcome.Passed => "[green]✓[/]",
        Outcome.Failed => "[red]✗[/]",
        Outcome.Cancelled => "[yellow]⊘[/]",
        Outcome.Skipped => "[grey]⊘[/]",
        Outcome.Running => "[green]●[/]",
        Outcome.Attention => "[yellow]●[/]",
        _ => "[grey]○[/]",
    };

    public static Outcome FromExitCode(int exitCode) => exitCode switch
    {
        0 => Outcome.Passed,
        130 => Outcome.Cancelled,
        _ => Outcome.Failed,
    };

    public static string Verdict(Outcome outcome, string text) => outcome switch
    {
        Outcome.Passed => $"[green]{Esc(text)}[/]",
        Outcome.Failed => $"[red]{Esc(text)}[/]",
        Outcome.Cancelled or Outcome.Attention => $"[yellow]{Esc(text)}[/]",
        _ => $"[grey]{Esc(text)}[/]",
    };

    public static void Heading(IAnsiConsole console, string subject, params string[] details)
    {
        console.MarkupLine(string.Join(Dot, [$"[bold]{Esc(subject)}[/]", .. details.Where(part => part.Length > 0)]));
        console.WriteLine();
    }

    public static void Fields(IAnsiConsole console, IEnumerable<(string Label, string Value)> rows)
    {
        var grid = new Grid()
            .AddColumn(new GridColumn().PadLeft(2).PadRight(2).NoWrap())
            .AddColumn(new GridColumn().PadLeft(0).PadRight(0));
        foreach (var (label, value) in rows) grid.AddRow(new Markup($"[grey]{Esc(label)}[/]"), new Markup(value));
        console.Write(grid);
    }

    public static void Rows(IAnsiConsole console, IEnumerable<string[]> rows, params int[] rightAligned)
    {
        var materialized = rows.ToArray();
        if (materialized.Length == 0) return;
        var grid = new Grid();
        for (var column = 0; column < materialized.Max(row => row.Length); column++)
        {
            var definition = new GridColumn().PadLeft(column == 0 ? 2 : 0).PadRight(3).NoWrap();
            if (rightAligned.Contains(column)) definition.RightAligned();
            grid.AddColumn(definition);
        }
        foreach (var row in materialized) grid.AddRow(row.Select(cell => new Markup(cell)).ToArray<Spectre.Console.Rendering.IRenderable>());
        console.Write(grid);
    }

    public static void Hint(IAnsiConsole console, string command) =>
        console.MarkupLine($"  [grey]→[/] [cyan]{Esc(command)}[/]");

    public static void Error(TextWriter writer, string message) =>
        Create(writer).MarkupLine($"[red]✗[/] {Esc(message)}");

    public static string Path(RepositoryContext repository, string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var relative = System.IO.Path.GetRelativePath(repository.Root, full);
        if (!relative.StartsWith("..", StringComparison.Ordinal) && !System.IO.Path.IsPathRooted(relative))
            return relative == "." ? "." : relative.Replace('\\', '/');
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 0 && full.StartsWith(home + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? "~" + full[home.Length..] : full;
    }

    public static string Duration(TimeSpan elapsed) => elapsed.TotalSeconds switch
    {
        < 1 => "<1s",
        < 60 => $"{(int)elapsed.TotalSeconds}s",
        < 3600 => $"{(int)elapsed.TotalMinutes}m{elapsed.Seconds:00}s",
        _ => $"{(int)elapsed.TotalHours}h{elapsed.Minutes:00}m",
    };

    public static string ShortRevision(string? value) =>
        value is { Length: 40 } && value.All(char.IsAsciiHexDigit) ? value[..7] : value ?? "";

    public static string Excerpt(string text)
    {
        var line = text.Split('\n')[0].Trim();
        return line.Length > 60 ? line[..60] + "…" : line;
    }

    public static string Number(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    public static string Delta(int value) => value switch
    {
        > 0 => "+" + Number(value),
        < 0 => Number(value),
        _ => "±0",
    };

    public static async Task<T> SpinAsync<T>(IAnsiConsole console, string status, Func<Task<T>> action)
    {
        if (!console.Profile.Capabilities.Interactive || !console.Profile.Capabilities.Ansi) return await action();
        var clock = Stopwatch.StartNew();
        return await console.Status()
            .AutoRefresh(false)
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(new Style(Color.Grey))
            .StartAsync(status, async context =>
            {
                var task = action();
                context.Refresh();
                while (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(1))) != task)
                {
                    context.Status($"{status} [grey]{Duration(clock.Elapsed)}[/]");
                    context.Refresh();
                }
                return await task;
            });
    }

    public static async Task SpinAsync(IAnsiConsole console, string status, Func<Task> action) =>
        await SpinAsync(console, status, async () => { await action(); return 0; });
}

internal sealed class TrailingSpaceTrimmer(TextWriter inner) : TextWriter
{
    private int pendingSpaces;

    public override System.Text.Encoding Encoding => inner.Encoding;

    public override void Write(char value)
    {
        if (value == ' ')
        {
            pendingSpaces++;
            return;
        }
        if (value is not ('\n' or '\r') && pendingSpaces > 0) inner.Write(new string(' ', pendingSpaces));
        pendingSpaces = 0;
        inner.Write(value);
    }

    public override void Write(string? value)
    {
        foreach (var character in value ?? "") Write(character);
    }

    public override void Flush() => inner.Flush();
}
