using System.CommandLine;
using Spectre.Console;

namespace Orisumi.Dev;

internal sealed record LocRoleRow(string Role, int Files, int Lines);

internal sealed record LocRow(string Name, int Files, int Implementation, int Tests);

internal sealed record LocReport(
    LocRow Code,
    IReadOnlyList<LocRoleRow> Roles,
    IReadOnlyList<LocRow> Modules,
    IReadOnlyList<LocRow> Languages,
    IReadOnlyList<string> UnclassifiedPaths)
{
    public const string CrossModule = "Cross-module";

    public static LocReport Create(SourceSnapshot snapshot, ProjectLayout layout)
    {
        var code = snapshot.Files
            .Where(file => file.Role is SourceRole.Production or SourceRole.Test or SourceRole.Tooling)
            .ToArray();
        var roles = snapshot.Files
            .GroupBy(file => file.Role)
            .OrderBy(group => group.Key)
            .Select(group => new LocRoleRow(group.Key.ToString(), group.Count(), group.Sum(file => file.Lines)))
            .ToArray();
        var modules = Rows(code, file => ModuleFor(layout.Owner(file.Path)!, layout))
            .OrderBy(row => row.Name == CrossModule)
            .ThenByDescending(row => row.Implementation)
            .ThenBy(row => row.Name, StringComparer.Ordinal)
            .ToArray();
        var languages = Rows(code, file => file.Language)
            .OrderByDescending(row => row.Implementation)
            .ThenBy(row => row.Name, StringComparer.Ordinal)
            .ToArray();
        return new LocReport(Row("Total", code), roles, modules, languages, snapshot.UnclassifiedPaths);
    }

    private static IEnumerable<LocRow> Rows(IEnumerable<SourceFileMetric> files, Func<SourceFileMetric, string> key) =>
        files.GroupBy(key, StringComparer.Ordinal).Select(group => Row(group.Key, [.. group]));

    private static LocRow Row(string name, SourceFileMetric[] files) => new(
        name,
        files.Length,
        files.Where(file => file.Role != SourceRole.Test).Sum(file => file.Lines),
        files.Where(file => file.Role == SourceRole.Test).Sum(file => file.Lines));

    private static string ModuleFor(Project owner, ProjectLayout layout)
    {
        if (owner.Role != SourceRole.Test) return owner.Name;
        var referenced = layout.Projects
            .Where(project => project.Role != SourceRole.Test && owner.References.Contains(project.Directory))
            .Select(project => project.Name)
            .ToArray();
        return referenced is [var module] ? module : CrossModule;
    }
}

internal sealed class LocCommand(RepositoryContext repository, TextWriter output)
{
    private const int BarWidth = 30;

    public Command CreateCommand()
    {
        var command = new Command("loc", "Count current effective LOC without running tests or analyzers");
        var json = CommandLine.Json();
        command.Options.Add(json);
        command.SetAction((parse, token) => RunAsync(parse.GetValue(json), token));
        return command;
    }

    private async Task<int> RunAsync(bool json, CancellationToken cancellationToken)
    {
        var layout = ProjectLayout.Load(repository.Root);
        var report = LocReport.Create(await SourceSnapshot.ReadAsync(repository.Root, layout, cancellationToken), layout);
        if (json) await CommandLine.WriteJsonAsync(output, report);
        else Write(Terminal.Create(output), report);
        return 0;
    }

    internal static void Write(IAnsiConsole console, LocReport report)
    {
        var roles = report.Roles.ToDictionary(row => row.Role, row => row, StringComparer.Ordinal);
        int Lines(SourceRole role) => roles.GetValueOrDefault(role.ToString())?.Lines ?? 0;
        var outside = Lines(SourceRole.Documentation) + Lines(SourceRole.Build) + Lines(SourceRole.Generated);

        Terminal.Heading(console, "Effective LOC", "worktree", "[grey]blank and comment-only lines excluded[/]");
        var fields = new List<(string, string)>
        {
            ("Code", $"{Terminal.Number(report.Code.Implementation + report.Code.Tests),7}   "
                + $"[grey]{Terminal.Number(report.Code.Implementation)} impl + {Terminal.Number(report.Code.Tests)} tests[/]"),
            ("Outside code", $"{Terminal.Number(outside),7}   [grey]{Terminal.Number(Lines(SourceRole.Documentation))} docs · "
                + $"{Terminal.Number(Lines(SourceRole.Build))} build · {Terminal.Number(Lines(SourceRole.Generated))} generated[/]"),
        };
        if (roles.GetValueOrDefault(nameof(SourceRole.Unclassified)) is { } unclassified)
            fields.Add(("Unclassified", $"[yellow]{Terminal.Number(unclassified.Lines),7}[/]   [grey]{Terminal.Number(unclassified.Files)} files, listed at the end[/]"));
        Terminal.Fields(console, fields);

        var rows = report.Modules.Concat(report.Languages).ToArray();
        var width = Math.Max(rows.Max(row => (int?)row.Name.Length) ?? 0, "Language".Length);
        var linesPerCell = Math.Max(rows.Max(row => (int?)(row.Implementation + row.Tests)) ?? 0, 1) / (double)BarWidth;
        console.WriteLine();
        WriteTable(console, "Module", $"[{Implementation}]█[/] impl  [{Tests}]░[/] tests", report.Modules, report.Code, width, linesPerCell);
        console.WriteLine();
        WriteTable(console, "Language", "", report.Languages, report.Code, width, linesPerCell);

        if (report.UnclassifiedPaths.Count > 0)
        {
            console.WriteLine();
            console.MarkupLine("[yellow]Unclassified files[/]");
            foreach (var path in report.UnclassifiedPaths) console.MarkupLine($"  {Terminal.Esc(path)}");
        }
    }

    private const string Implementation = "deepskyblue3";
    private const string Tests = "green";

    private static void WriteTable(
        IAnsiConsole console, string title, string legend, IReadOnlyList<LocRow> rows, LocRow total, int width,
        double linesPerCell)
    {
        console.MarkupLine($"  [grey]{title.PadRight(width)} {"Impl",8} {"Tests",8}[/]" + (legend.Length > 0 ? "   " + legend : ""));
        foreach (var row in rows)
        {
            console.MarkupLine(Columns(row, width) + "   "
                + $"[{Implementation}]{Bar(row.Implementation, linesPerCell, '█', "▏")}[/]"
                + $"[{Tests}]{Bar(row.Tests, linesPerCell, '░', "░")}[/]");
        }
        console.MarkupLine($"[bold]{Columns(total, width)}[/]");
    }

    private static string Columns(LocRow row, int width) =>
        $"  {Terminal.Esc(row.Name.PadRight(width))} {Count(row.Implementation)} {Count(row.Tests)}";

    private static string Count(int lines) => lines == 0 ? new string(' ', 7) + "[grey]·[/]" : Terminal.Number(lines).PadLeft(8);

    private static string Bar(int lines, double linesPerCell, char cell, string sliver)
    {
        if (lines == 0) return "";
        var cells = (int)Math.Round(lines / linesPerCell);
        return cells == 0 ? sliver : new string(cell, cells);
    }
}
