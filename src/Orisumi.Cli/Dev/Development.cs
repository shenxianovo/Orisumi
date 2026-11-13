using System.Diagnostics;
using Spectre.Console;

namespace Orisumi.Cli.Dev;

internal static class Development
{
    internal static async Task<int> Run(string executable, string directory, CancellationToken token, params string[] arguments)
    {
        token.ThrowIfCancellationRequested();
        AnsiConsole.Write(new Text($"{executable} {string.Join(' ', arguments)}{Environment.NewLine}", new Style(Color.Blue)));
        var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new IOException($"无法启动 {executable}。");
        try
        {
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        return process.ExitCode;
    }
}
