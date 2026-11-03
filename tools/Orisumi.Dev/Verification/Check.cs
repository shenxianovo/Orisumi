using Spectre.Console;

namespace Orisumi.Dev;

internal interface ICheck
{
    string Name { get; }

    string Description { get; }

    Task<CheckOutcome> RunAsync(Run run, CancellationToken cancellationToken);
}

internal sealed record CheckOutcome(int ExitCode, string Note = "", object? Report = null)
{
    public Outcome Mark { get; init; } = Terminal.FromExitCode(ExitCode);

    public Action<IAnsiConsole> Details { get; init; } = _ => { };
}

internal sealed record CheckRecord(string Name, int ExitCode, string? Log, object? Report);

internal sealed class ProcessCheck(string name, ProcessCommand command, IProcessRunner runner, RepositoryContext repository) : ICheck
{
    private const int FailureLines = 20;

    public string Name => name;

    public string Description =>
        string.Join(' ', [command.FileName, .. command.Arguments.Select(Relative)])
        + (command.WorkingDirectory is { } directory ? $"  (in {Relative(directory)})" : "");

    public async Task<CheckOutcome> RunAsync(Run run, CancellationToken cancellationToken)
    {
        run.Commands.Add(command.ToString());
        var log = run.Log(name);
        ProcessResult result;
        try
        {
            result = await runner.CaptureAsync(command, cancellationToken);
        }
        catch (CapturedProcessCancelledException cancelled)
        {
            await File.WriteAllTextAsync(log, cancelled.StdOut + cancelled.StdErr, CancellationToken.None);
            throw;
        }
        await File.WriteAllTextAsync(log, result.Output, CancellationToken.None);
        if (result.ExitCode is 0 or 130) return new CheckOutcome(result.ExitCode);
        return new CheckOutcome(result.ExitCode, $"[grey]exit {result.ExitCode}[/]")
        {
            Details = console => WriteFailure(console, result.StdErr.Length > 0 ? result.StdErr : result.StdOut, log),
        };
    }

    private void WriteFailure(IAnsiConsole console, string text, string log)
    {
        foreach (var line in text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Trim().Length > 0).TakeLast(FailureLines))
            console.MarkupLine($"      [grey]{Terminal.Esc(line)}[/]");
        console.MarkupLine($"      [grey]log[/] {Terminal.Esc(Terminal.Path(repository, log))}");
    }

    private string Relative(string argument) =>
        Path.IsPathRooted(argument) && argument.StartsWith(repository.Root, StringComparison.Ordinal)
            ? Terminal.Path(repository, argument) : argument;
}
