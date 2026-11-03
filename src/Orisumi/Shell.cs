using System.Diagnostics;

namespace Orisumi;

internal static class Shell
{
    public static async Task<string> CaptureAsync(string file, IEnumerable<string> arguments, string? workingDirectory = null)
    {
        using var writer = new StringWriter();
        await RunAsync(file, arguments, null, writer, workingDirectory);
        return writer.ToString();
    }

    public static async Task ToFileAsync(string file, IEnumerable<string> arguments, string? input, string target)
    {
        await using var writer = new StreamWriter(target);
        await RunAsync(file, arguments, input, writer, null);
    }

    private static async Task RunAsync(string file, IEnumerable<string> arguments, string? input, TextWriter output, string? workingDirectory)
    {
        var start = new ProcessStartInfo
        {
            FileName = file,
            UseShellExecute = false,
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        if (input is not null)
        {
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
        }
        var buffer = new char[81920];
        int read;
        while ((read = await process.StandardOutput.ReadAsync(buffer)) > 0) await output.WriteAsync(buffer.AsMemory(0, read));
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{file} {string.Join(' ', start.ArgumentList)} exited {process.ExitCode}: {await error}");
    }
}
