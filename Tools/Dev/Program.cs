using System.CommandLine;
using System.ComponentModel;
using System.Text.Json;
using Orisumi.Dev;

try
{
    if (await SourceLauncher.TryRun(args) is int launched) return launched;

    var root = new RootCommand("Orisumi · 开发与部署")
    {
        DevCommand.Create(), CheckCommand.Create(),
    };
    DevPlatform.AddCommands(root);
    root.HelpName = "orisumi-dev";

    var command = args.Length > 0 ? args[0] : null;
    if (command is not (null or "-h" or "-?" or "--help" or "--version") &&
        !root.Subcommands.Any(item => item.Name == command))
        return await DevInstance.Forward(args);

    return await root.Parse(args).InvokeAsync(new InvocationConfiguration
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
    Console.Error.WriteLine($"orisumi-dev: {error.Message}");
    return error is ArgumentException ? 2 : 1;
}
