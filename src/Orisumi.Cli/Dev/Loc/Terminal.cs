using System.Globalization;
using Spectre.Console;

namespace Orisumi.Cli.Dev.Loc;

internal static class Terminal
{
    public static IAnsiConsole Create(TextWriter writer)
    {
        var terminal = Environment.GetEnvironmentVariable("TERM") != "dumb"
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))
            && (ReferenceEquals(writer, Console.Out) ? !Console.IsOutputRedirected
                : ReferenceEquals(writer, Console.Error) && !Console.IsErrorRedirected);
        var noColor = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(terminal ? writer : new TrailingSpaceTrimmer(writer)),
            Ansi = terminal ? AnsiSupport.Detect : AnsiSupport.No,
            ColorSystem = terminal && !noColor ? ColorSystemSupport.Detect : ColorSystemSupport.NoColors,
            Interactive = terminal ? InteractionSupport.Detect : InteractionSupport.No,
        });
        if (!terminal) console.Profile.Width = 240;
        return console;
    }

    public static string Esc(string text) => Markup.Escape(text);

    public static void Heading(IAnsiConsole console, string subject, params string[] details)
    {
        console.MarkupLine(string.Join(" [grey]·[/] ", [$"[bold]{Esc(subject)}[/]", .. details.Where(part => part.Length > 0)]));
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

    public static string Number(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
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
