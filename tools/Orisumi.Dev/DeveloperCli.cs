using System.CommandLine;
using System.CommandLine.Help;
using System.Text.Json;

namespace Orisumi.Dev;

internal sealed class DeveloperCli(
    RepositoryContext repository,
    IProcessRunner runner,
    TextWriter output,
    TextWriter error,
    ModelAccess? models = null,
    TextReader? input = null)
{
    private readonly ModelAccess _models = models ?? ModelAccess.Default;

    internal Command CreateCommand()
    {
        var root = CommandLine.Group("orisumi", "Orisumi development and verification");
        root.Options.Add(new HelpOption { Recursive = true });
        root.Options.Add(new VersionOption());
        root.Subcommands.Add(new DocsCommand(repository, runner, output).CreateCommand());
        root.Subcommands.Add(new TidyCommand(repository, runner, output).CreateCommand());
        root.Subcommands.Add(new VerificationCommand(repository, runner, output, error).CreateCommand());
        root.Subcommands.Add(new ReviewCommand(repository, runner, _models, output).CreateCommand());
        root.Subcommands.Add(new LocCommand(repository, output).CreateCommand());
        root.Subcommands.Add(new RunsCommand(repository, output).CreateCommand());
        root.Subcommands.Add(new ConfigCommand(repository, _models.Config, output, input ?? Console.In).CreateCommand());
        root.Subcommands.Add(CommandLine.Group("tool", "Install or update the developer tool itself",
            new InstallCommand(repository, runner, output).CreateCommand()));
        return root;
    }

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var parsed = CreateCommand().Parse(args);
        var configuration = new InvocationConfiguration
        {
            Output = output,
            Error = error,
            EnableDefaultExceptionHandler = false,
            ProcessTerminationTimeout = null,
        };
        try
        {
            var result = await parsed.InvokeAsync(configuration, cancellationToken);
            return parsed.Errors.Count > 0 ? 2 : result;
        }
        catch (CommandUsageException exception)
        {
            Terminal.Error(error, exception.Message);
            return 2;
        }
    }
}

internal static class CommandLine
{
    public static Command Group(string name, string description, params Command[] commands)
    {
        var group = new Command(name, description);
        group.SetAction(parse => new HelpAction().Invoke(parse));
        foreach (var command in commands) group.Subcommands.Add(command);
        return group;
    }

    public static Option<bool> Json() => new("--json") { Description = "Print JSON" };

    public static Task WriteJsonAsync(TextWriter output, object? value) =>
        output.WriteLineAsync(JsonSerializer.Serialize(value, JsonOptions.Indented));
}

internal sealed class CommandUsageException(string message) : Exception(message);
