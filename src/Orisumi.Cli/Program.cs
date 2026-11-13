using System.CommandLine;
using System.ComponentModel;
using System.Text.Json;
using Orisumi.Cli;
using Spectre.Console;

try
{
    if (await SourceLauncher.TryRun(args) is int exitCode)
        return exitCode;

    return await Commands.Create().Parse(args).InvokeAsync(new InvocationConfiguration
    {
        EnableDefaultExceptionHandler = false,
        ProcessTerminationTimeout = TimeSpan.FromSeconds(10),
    });
}
catch (OperationCanceledException)
{
    return 130;
}
catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or
    InvalidDataException or JsonException or Win32Exception)
{
    var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });
    console.Write(new Text($"orisumi: {error.Message}{Environment.NewLine}", new Style(Color.Red)));
    return error is ArgumentException ? 2 : 1;
}
