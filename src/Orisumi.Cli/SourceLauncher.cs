using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Orisumi.Cli;

internal static class SourceLauncher
{
    internal static async Task<int?> TryRun(string[] arguments)
    {
        // Source builds also serve Chrome and background collectors outside the checkout's cwd.
        // Only the installed tool delegates; its child executes these commands directly.
        var sourceRoot = Repository.Find(AppContext.BaseDirectory);
        if (sourceRoot is not null && AppContext.BaseDirectory.StartsWith(
                Path.Combine(sourceRoot, "src/Orisumi.Cli/bin") + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return null;

        var root = Repository.Require();
        var project = Path.Combine(root, Repository.CliProject);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onInterrupt = (_, signal) =>
        {
            signal.Cancel = true;
            cancellation.Cancel();
        };
        using var termination = OperatingSystem.IsWindows() ? null :
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal =>
            {
                signal.Cancel = true;
                cancellation.Cancel();
            });
        Console.CancelKeyPress += onInterrupt;
        try
        {
            // Build logs must never corrupt command stdout (in particular Native Messaging).
            var build = await Run(["msbuild", project, "-restore", "-target:Build", "-property:Configuration=Debug",
                "-nologo", "-verbosity:quiet", "-consoleLoggerParameters:NoSummary", "-tl:off"],
                Console.Error, cancellation.Token);
            if (build != 0)
                return build;

            // Ask MSBuild for the output path so SDK/framework changes in the checkout take effect too.
            using var target = new StringWriter(CultureInfo.InvariantCulture);
            var query = await Run(["msbuild", project, "-nologo", "-property:Configuration=Debug", "-getProperty:TargetPath"],
                target, cancellation.Token);
            if (query != 0)
            {
                await Console.Error.WriteAsync(target.ToString());
                return query;
            }

            var assembly = target.ToString().Trim();
            if (!File.Exists(assembly))
                throw new IOException("构建未生成可运行的 Orisumi.Cli 程序。");
            return await Run([assembly, .. arguments], output: null, cancellation.Token);
        }
        finally
        {
            Console.CancelKeyPress -= onInterrupt;
        }
    }

    private static async Task<int> Run(string[] arguments, TextWriter? output, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet"));
        if (OperatingSystem.IsWindows())
            dotnet += ".exe";
        var start = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory,
            RedirectStandardOutput = output is not null,
            RedirectStandardInput = output is not null,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new IOException("无法启动 .NET。");
        if (start.RedirectStandardInput)
            process.StandardInput.Close();
        var copy = output is null ? Task.CompletedTask : CopyOutput(process.StandardOutput, output);
        var exited = process.WaitForExitAsync();
        try
        {
            await exited.WaitAsync(token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                if (!OperatingSystem.IsWindows())
                {
                    // Let collectors flush and record the end of their capture run before escalating.
                    var signal = new ProcessStartInfo("/bin/kill") { UseShellExecute = false };
                    signal.ArgumentList.Add("-TERM");
                    signal.ArgumentList.Add(process.Id.ToString(CultureInfo.InvariantCulture));
                    using var kill = Process.Start(signal);
                    if (kill is not null)
                        await kill.WaitForExitAsync();
                    await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(5)));
                }
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            await exited;
            await copy;
            return 130;
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
