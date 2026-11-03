using System.CommandLine;
using System.CommandLine.Help;

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
        var root = new Command("orisumi", "Orisumi development and verification")
        {
            new HelpOption { Recursive = true },
            new VersionOption(),
            new DocsCommand(repository, runner, output).CreateCommand(),
            new VerificationCommand(repository, runner, output, error, _models).CreateCommand(),
            new ReviewCommand(repository, runner, _models, output).CreateCommand(),
            new LintCommand(repository, runner, output).CreateCommand(),
            Group("analyze", "Analyze code structure and size",
                new QualityCommand(repository, runner, output).CreateCommand(),
                new LocCommand(repository, runner, output).CreateCommand()),
            new RunsCommand(repository, output).CreateCommand(),
            new ConfigCommand(repository, _models.Config, output, input ?? Console.In).CreateCommand(),
            Group("tool", "Install or update the developer tool itself",
                new InstallCommand(repository, runner, output).CreateCommand()),
        };
        root.SetAction(parse => new HelpAction().Invoke(parse));
        return root;
    }

    internal static Command Group(string name, string description, params Command[] commands)
    {
        var group = new Command(name, description);
        group.SetAction(parse => new HelpAction().Invoke(parse));
        foreach (var command in commands) group.Subcommands.Add(command);
        return group;
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

internal sealed class CommandUsageException(string message) : Exception(message);
