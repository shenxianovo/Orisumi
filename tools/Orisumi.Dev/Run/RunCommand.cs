using System.CommandLine;

namespace Orisumi.Dev;

internal sealed class RunCommand(RepositoryContext repository, IProcessRunner runner, TextWriter output)
{
    public const string Name = "run";

    public Command CreateCommand()
    {
        var command = new Command(Name, "Run Orisumi from this checkout with its store in .local/orisumi; arguments after run go to Orisumi");
        command.SetAction((_, token) => ForwardAsync([], token));
        return command;
    }

    public Task<int> ForwardAsync(IReadOnlyList<string> arguments, CancellationToken token) =>
        runner.StreamAsync(new("dotnet",
            ["run", "--project", repository.Path("src", "Orisumi"), "--verbosity", "quiet", "--", .. arguments],
            Environment: new Dictionary<string, string?> { ["ORISUMI_STORE"] = repository.Path(".local", "orisumi") }), output, token);
}
