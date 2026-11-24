using System.Diagnostics;
using Spectre.Console;

namespace Orisumi.Dev;

internal static class Development
{
    internal static (string Executable, string[] Arguments) Command(string executable, string[] arguments) =>
        executable == "npm" ? DevPlatform.Npm(arguments) : (executable, arguments);

    internal static async Task<int> Run(string executable, string directory, CancellationToken token, params string[] arguments)
    {
        token.ThrowIfCancellationRequested();
        AnsiConsole.Write(new Text($"{executable} {string.Join(' ', arguments)}{Environment.NewLine}", new Style(Color.Blue)));
        (executable, arguments) = Command(executable, arguments);
        var start = new ProcessStartInfo(executable, arguments) { WorkingDirectory = directory, UseShellExecute = false };
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
