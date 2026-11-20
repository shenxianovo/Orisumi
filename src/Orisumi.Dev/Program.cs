using System.CommandLine;
using System.ComponentModel;
using System.Text.Json;
using Orisumi.Dev;
using Orisumi.Platform;

try
{
    if (await SourceLauncher.TryRun(args) is int launched) return launched;

    var root = new RootCommand("Orisumi · 开发、发布与预览") { DevCommand.Create(), DevCommand.Extension() };
    root.HelpName = "orisumi-dev";
    ReleaseCommands.AddTo(root);

    var index = 0;
    while (index < args.Length)
    {
        var option = args[index];
        if (option is "--releases" or "--prod" or "--preview") index += 2;
        else if (option.Split('=', ':')[0] is "--releases" or "--prod" or "--preview") index++;
        else break;
    }
    var command = index < args.Length ? args[index] : null;
    if (command is not (null or "-h" or "-?" or "--help" or "--version") &&
        !root.Subcommands.Any(item => item.Name == command))
    {
        var checkout = Repository.Require();
        var output = Path.GetRelativePath(Path.Combine(SourceLauncher.BuiltCheckout()!, Path.GetDirectoryName(Repository.DevProject)!),
            AppContext.BaseDirectory);
        var host = Path.Combine(checkout, Path.GetDirectoryName(Repository.CliProject)!, output, "orisumi.dll");
        var hasStore = args.TakeWhile(arg => arg != "--").Any(arg => arg is "--store" ||
            arg.StartsWith("--store=", StringComparison.Ordinal) || arg.StartsWith("--store:", StringComparison.Ordinal));
        return await RunProcess.WithCancellation(token => RunProcess.Run(RunProcess.Dotnet,
            hasStore ? [host, .. args] : [host, "--store", Instance.Development(checkout).Store.Root, .. args], null, token, preserveExitCode: true));
    }

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
