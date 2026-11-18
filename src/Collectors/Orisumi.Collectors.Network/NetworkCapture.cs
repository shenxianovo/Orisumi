using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Orisumi.Collectors.Network;

public static class NetworkCapture
{
    public static async Task Run(string store, int? seconds, int intervalSeconds, TextWriter log)
    {
        if (seconds is <= 0 || intervalSeconds <= 0) throw new ArgumentException("Duration and interval must be positive.");
        var path = NetworkDatabase.PathFor(store);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var captureLock = new FileStream(path + ".capture.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var boot = (await Execute("/usr/sbin/sysctl", ["-n", "kern.bootsessionuuid"], CancellationToken.None)).Trim();
        if (!Guid.TryParse(boot, out _)) throw new IOException("Unable to identify the current macOS boot.");
        using var stop = new CancellationTokenSource();
        if (seconds is int duration) stop.CancelAfter(TimeSpan.FromSeconds(duration));
        ConsoleCancelEventHandler onInterrupt = (_, signal) => { signal.Cancel = true; stop.Cancel(); };
        using var termination = PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal => { signal.Cancel = true; stop.Cancel(); });
        Console.CancelKeyPress += onInterrupt;
        using var database = new NetworkDatabase(store, boot, intervalSeconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var clock = Stopwatch.StartNew();
        var reason = "stopped";
        log.WriteLine($"Sampling nettop every {intervalSeconds}s → {path}");
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var cycleStarted = clock.Elapsed;
                await Sample(database, intervalSeconds, clock,
                    token => Execute("/usr/bin/nettop", ["-L", "1", "-n", "-x", "-J", "interface,state,bytes_in,bytes_out"], token), log, stop.Token);
                var delay = TimeSpan.FromSeconds(intervalSeconds) - (clock.Elapsed - cycleStarted);
                if (delay > TimeSpan.Zero) await Task.Delay(delay, stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch { reason = "failed"; throw; }
        finally
        {
            Console.CancelKeyPress -= onInterrupt;
            database.Stop(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), reason);
        }
    }

    internal static async Task Sample(NetworkDatabase database, int intervalSeconds, Stopwatch clock,
        Func<CancellationToken, Task<string>> read, TextWriter log, CancellationToken stop)
    {
        var startedAt = DateTimeOffset.UtcNow;
        List<ProcessSample> processes = [];
        string? error = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(intervalSeconds, 10)));
            var csv = await read(timeout.Token);
            processes = NettopSnapshot.Parse(csv, pid => NettopSnapshot.Identify(pid, startedAt));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or OperationCanceledException or Microsoft.VisualBasic.FileIO.MalformedLineException)
        {
            error = exception is OperationCanceledException ? "nettop sample timed out" : exception.Message;
            log.WriteLine($"Network sample failed: {error}");
        }
        database.Sample(startedAt.ToUnixTimeMilliseconds(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            clock.ElapsedMilliseconds, processes, error);
    }

    private static async Task<string> Execute(string executable, string[] arguments, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.Environment["LC_ALL"] = "C";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException($"Cannot start {executable}");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var interrupt = cancellation.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        });
        await process.WaitForExitAsync();
        var result = await stdout;
        var errors = await stderr;
        cancellation.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new IOException($"{Path.GetFileName(executable)} exited {process.ExitCode}: {errors.Trim()}");
        return result;
    }
}
