using System.CommandLine;
using System.Runtime.InteropServices;
using Spectre.Console;

namespace Orisumi.Dev;

internal sealed class PublishCommand(RepositoryContext repository, IProcessRunner runner, TextWriter output)
{
    public Command CreateCommand()
    {
        var directory = new Argument<string>("directory") { Description = "尚不存在的发布目录" };
        var command = new Command("publish", "将本检出发布为本机可独立运行的 CLI") { directory };
        command.SetAction((parse, token) => PublishAsync(parse.GetValue(directory)!, token));
        return command;
    }

    private async Task<int> PublishAsync(string directory, CancellationToken token)
    {
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (Path.Exists(destination)) throw new CommandUsageException($"发布目录须是新路径：{destination}");
        var staging = Path.Combine(Path.GetDirectoryName(destination)!, $".orisumi-publish-{Guid.NewGuid():N}");
        var console = Terminal.Create(output);
        var runtime = RuntimeInformation.RuntimeIdentifier;
        Directory.CreateDirectory(staging);
        try
        {
            var result = await Terminal.SpinAsync(console, $"发布本机 CLI（{runtime}）", () => runner.CaptureAsync(new("dotnet",
                ["publish", repository.Path("src", "Orisumi.Cli"), "--configuration", "Release", "--runtime", runtime,
                    "--self-contained", "true", "-p:PublishTrimmed=false", "--output", staging]), token));
            if (result.ExitCode != 0)
            {
                console.MarkupLine($"{Terminal.Mark(Outcome.Failed)} 发布失败 [grey](exit {result.ExitCode})[/]");
                console.WriteLine(result.Output);
                return result.ExitCode;
            }
            var executable = OperatingSystem.IsWindows() ? "orisumi-cli.exe" : "orisumi-cli";
            if (!File.Exists(Path.Combine(staging, executable))) throw new IOException($"发布结果缺少 {executable}");
            if (OperatingSystem.IsMacOS() && !File.Exists(Path.Combine(staging, "orisumi-foreground")))
                throw new IOException("发布结果缺少 orisumi-foreground");
            token.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            console.MarkupLine($"{Terminal.Mark(Outcome.Passed)} 已发布 [grey]{Terminal.Esc(Terminal.Path(repository, destination))} · {runtime}[/]");
            return 0;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }
}
