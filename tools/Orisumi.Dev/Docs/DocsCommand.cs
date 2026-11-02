using System.CommandLine;
using System.CommandLine.Help;
using Spectre.Console;

namespace Orisumi.Dev;

/// Runs the docs site of this checkout.
internal sealed class DocsCommand(RepositoryContext repository, IProcessRunner runner, TextWriter output)
{
    internal string Site => repository.Path("src", "Docs", "Orisumi.Docs");

    public Command CreateCommand()
    {
        var command = new Command("docs", "Run the docs site of this checkout");
        command.SetAction(parse => new HelpAction().Invoke(parse));
        var dev = new Command("dev", "Install the docs dependencies and run the dev server until Ctrl+C");
        dev.SetAction((_, token) => DevAsync(token));
        command.Subcommands.Add(dev);
        return command;
    }

    internal async Task<int> DevAsync(CancellationToken token)
    {
        var console = Terminal.Create(output);
        // pnpm leaves node_modules alone when it already matches the lockfile.
        var install = await Terminal.SpinAsync(console, "Installing docs dependencies",
            () => runner.CaptureAsync("pnpm", ["--dir", Site, "install", "--frozen-lockfile"], null, token));
        if (install.ExitCode != 0)
        {
            console.MarkupLine($"{Terminal.Mark(Outcome.Failed)} Installing docs dependencies failed [grey](exit {install.ExitCode})[/]");
            console.WriteLine(install.StdOut + install.StdErr);
            return install.ExitCode;
        }
        console.MarkupLine($"{Terminal.Mark(Outcome.Running)} Docs dev server [grey]{Terminal.Esc(Terminal.Path(repository, Site))} · Ctrl+C stops it[/]");
        return await runner.StreamAsync("pnpm", ["--dir", Site, "dev"], output, token);
    }
}
