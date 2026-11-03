using System.CommandLine;
using Spectre.Console;

namespace Orisumi.Dev;

internal sealed class DocsCommand(RepositoryContext repository, IProcessRunner runner, TextWriter output)
{
    internal string Site => repository.Path("src", "Docs", "Orisumi.Docs");

    public Command CreateCommand()
    {
        var dev = new Command("dev", "Install the docs dependencies and run the dev server until Ctrl+C");
        dev.SetAction((_, token) => DevAsync(token));
        return CommandLine.Group("docs", "Run the docs site of this checkout", dev);
    }

    internal async Task<int> DevAsync(CancellationToken token)
    {
        var console = Terminal.Create(output);
        var install = await Terminal.SpinAsync(console, "Installing docs dependencies",
            () => runner.CaptureAsync(new("pnpm", ["--dir", Site, "install", "--frozen-lockfile"]), token));
        if (install.ExitCode != 0)
        {
            console.MarkupLine($"{Terminal.Mark(Outcome.Failed)} Installing docs dependencies failed [grey](exit {install.ExitCode})[/]");
            console.WriteLine(install.Output);
            return install.ExitCode;
        }
        console.MarkupLine($"{Terminal.Mark(Outcome.Running)} Docs dev server [grey]{Terminal.Esc(Terminal.Path(repository, Site))} · Ctrl+C stops it[/]");
        return await runner.StreamAsync(new("pnpm", ["--dir", Site, "dev"]), output, token);
    }
}
