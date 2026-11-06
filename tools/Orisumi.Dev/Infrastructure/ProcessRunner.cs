using System.Diagnostics;

namespace Orisumi.Dev;

internal sealed record ProcessCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string?>? Environment = null,
    TimeSpan? CancellationGracePeriod = null)
{
    public override string ToString() =>
        string.Join(' ', [FileName, .. Arguments.Select(argument => argument.Contains(' ', StringComparison.Ordinal) ? $"\"{argument}\"" : argument)]);
}

internal sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public string Output => StdOut + StdErr;

    public string LastLine =>
        (StdErr + Environment.NewLine + StdOut)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault()?.Trim() ?? "command failed";
}

internal sealed class CapturedProcessCancelledException(string stdout, string stderr, CancellationToken token)
    : OperationCanceledException("Captured process was cancelled.", token)
{
    public string StdOut { get; } = stdout;
    public string StdErr { get; } = stderr;
}

internal interface IProcessRunner
{
    async Task<int> StreamAsync(ProcessCommand command, TextWriter output, CancellationToken cancellationToken)
    {
        var result = await CaptureAsync(command, cancellationToken);
        await output.WriteAsync(result.Output);
        return result.ExitCode;
    }

    Task<ProcessResult> CaptureAsync(ProcessCommand command, CancellationToken cancellationToken);
}

internal sealed class ProcessRunner(string workingDirectory) : IProcessRunner
{
    public async Task<int> StreamAsync(ProcessCommand command, TextWriter output, CancellationToken cancellationToken)
    {
        using var process = Start(command);
        var writer = TextWriter.Synchronized(output);
        async Task CopyAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync(CancellationToken.None) is { } line) await writer.WriteLineAsync(line);
        }
        var copies = Task.WhenAll(CopyAsync(process.StandardOutput), CopyAsync(process.StandardError));
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            if (command.CancellationGracePeriod is { } grace)
            {
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(grace, CancellationToken.None); }
                catch (TimeoutException) { }
            }
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        finally { await copies; }
        return process.ExitCode;
    }

    public async Task<ProcessResult> CaptureAsync(ProcessCommand command, CancellationToken cancellationToken)
    {
        using var process = Start(command);
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            throw new CapturedProcessCancelledException(await stdout, await stderr, cancellationToken);
        }
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private Process Start(ProcessCommand command)
    {
        var start = new ProcessStartInfo
        {
            FileName = command.FileName,
            WorkingDirectory = command.WorkingDirectory ?? workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in command.Arguments) start.ArgumentList.Add(argument);
        foreach (var (name, value) in command.Environment ?? new Dictionary<string, string?>()) start.Environment[name] = value;
        var process = new Process { StartInfo = start };
        process.Start();
        return process;
    }

    private static void Kill(Process process)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
    }
}
