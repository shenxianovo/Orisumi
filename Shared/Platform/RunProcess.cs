using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Orisumi.Platform;

internal delegate Task<int> CommandProcess(string executable, string[] arguments, TextWriter? output, CancellationToken token);

public static partial class RunProcess
{
    public static string Dotnet => Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(),
        "..", "..", "..", DotnetFileName));

    internal static partial IDisposable RegisterTermination(CancellationTokenSource cancellation);
    private static partial Task WaitForChildShutdown(Process process, Task exited);

    public static async Task<T> WithCancellation<T>(Func<CancellationToken, Task<T>> run)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onInterrupt = (_, signal) =>
        {
            signal.Cancel = true;
            cancellation.Cancel();
        };
        using var termination = RegisterTermination(cancellation);
        Console.CancelKeyPress += onInterrupt;
        try { return await run(cancellation.Token); }
        finally
        {
            Console.CancelKeyPress -= onInterrupt;
        }
    }

    internal static Task<int> RunCommand(string executable, string[] arguments, TextWriter? output, CancellationToken token) =>
        Run(executable, arguments, output, token);

    public static async Task<int> Run(string executable, string[] arguments, TextWriter? output, CancellationToken token,
        bool preserveExitCode = false, string? directory = null, TextWriter? errors = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable, arguments)
        {
            UseShellExecute = false,
            WorkingDirectory = directory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = output is not null,
            RedirectStandardError = errors is not null,
            RedirectStandardInput = output is not null,
        };

        if (environment is not null)
            foreach (var (name, value) in environment)
                start.Environment[name] = value;

        using var process = Process.Start(start) ?? throw new IOException($"无法启动 {executable}。");
        if (start.RedirectStandardInput)
            process.StandardInput.Close();
        var copy = Task.WhenAll(
            output is null ? Task.CompletedTask : CopyOutput(process.StandardOutput, output),
            errors is null ? Task.CompletedTask : CopyOutput(process.StandardError, errors));
        var exited = process.WaitForExitAsync();
        try
        {
            await exited.WaitAsync(token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                // Let collectors flush and record the end of their capture run before escalating.
                await WaitForChildShutdown(process, exited);
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            await exited;
            await copy;
            return preserveExitCode ? process.ExitCode : 130;
        }
        await copy;
        return process.ExitCode;
    }

    private static async Task CopyOutput(TextReader input, TextWriter output)
    {
        var buffer = new char[4096];
        int count;
        while ((count = await input.ReadAsync(buffer)) != 0)
            await output.WriteAsync(buffer.AsMemory(0, count));
    }
}
