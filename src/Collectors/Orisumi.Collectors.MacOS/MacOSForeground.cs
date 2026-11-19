using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Orisumi.Collectors.Storage;

namespace Orisumi.Collectors.MacOS;

public static class MacOSForeground
{
    public static Task<int> Run(string store, int? seconds, bool captureWindows, int? afkThresholdSeconds,
        bool captureInput, bool captureMicrophone, bool captureAudioOutput, bool captureCamera)
        => RunObserver(Path.Combine(AppContext.BaseDirectory, "orisumi-macos-foreground"), store, seconds,
            captureWindows, afkThresholdSeconds, captureInput, captureMicrophone, captureAudioOutput, captureCamera);

    internal static async Task<int> RunObserver(string helper, string store, int? seconds, bool captureWindows,
        int? afkThresholdSeconds, bool captureInput, bool captureMicrophone, bool captureAudioOutput, bool captureCamera)
    {
        var start = new ProcessStartInfo(helper)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        if (seconds is int duration)
            start.ArgumentList.Add(duration.ToString(CultureInfo.InvariantCulture));
        if (captureWindows)
            start.ArgumentList.Add("--windows");
        if (afkThresholdSeconds is int threshold)
            start.ArgumentList.Add($"--afk-threshold={threshold.ToString(CultureInfo.InvariantCulture)}");
        if (captureInput)
            start.ArgumentList.Add("--input");
        if (captureMicrophone)
            start.ArgumentList.Add("--microphone");
        if (captureAudioOutput)
            start.ArgumentList.Add("--audio-output");
        if (captureCamera)
            start.ArgumentList.Add("--camera");

        var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ConsoleCancelEventHandler onInterrupt = (_, signal) =>
        {
            signal.Cancel = true;
            interrupted.TrySetResult();
        };
        using var termination = PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal =>
        {
            signal.Cancel = true;
            interrupted.TrySetResult();
        });
        Console.CancelKeyPress += onInterrupt;
        try
        {
            using var writer = new CaptureWriter(store, "macos", (command, run) => command.Execute("""
                INSERT INTO capture_run_configuration VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6)
                """, run, (afkThresholdSeconds ?? 300) * 1000L, captureWindows, captureInput,
                captureMicrophone, captureAudioOutput, captureCamera));
            try
            {
                using var process = Process.Start(start) ?? throw new IOException("could not start the macOS observer");
                using var finished = new CancellationTokenSource();
                var requestStop = RequestStop(process, interrupted.Task, finished.Token);
                try
                {
                    var stopped = await Receive(process.StandardOutput.BaseStream, writer);
                    await process.WaitForExitAsync();
                    if (process.ExitCode != 0 || !stopped)
                        throw new IOException($"macOS observer exited with code {process.ExitCode} without a successful stop");
                    writer.Stop();
                    return 0;
                }
                finally
                {
                    finished.Cancel();
                    // Closing the control pipe makes the helper exit even if its output worker is blocked.
                    await requestStop;
                    process.StandardInput.Close();
                    await process.WaitForExitAsync();
                }
            }
            catch (Exception error)
            {
                writer.Stop(error);
                Console.Error.WriteLine($"orisumi: macOS foreground capture failed: {error.Message}");
                return 1;
            }
        }
        finally
        {
            Console.CancelKeyPress -= onInterrupt;
        }
    }

    private static async Task RequestStop(Process process, Task interrupted, CancellationToken finished)
    {
        try
        {
            await interrupted.WaitAsync(finished);
            await process.StandardInput.WriteLineAsync("stop");
            await process.StandardInput.FlushAsync();
        }
        catch (OperationCanceledException) when (finished.IsCancellationRequested) { }
        catch (IOException) { } // The observer can finish while the stop command is in flight.
    }

    private static async Task<bool> Receive(Stream output, CaptureWriter writer)
    {
        const int maximumLine = 4 * 1024 * 1024;
        var observations = new MacOSObservations();
        var stopped = false;
        var bytes = new byte[64 * 1024];
        using var line = new MemoryStream();
        while (true)
        {
            int count;
            try { count = await output.ReadAsync(bytes); }
            catch (IOException error) { writer.RecordReadFailure(error); throw; }
            if (count == 0)
            {
                if (line.Length != 0) throw ReadFailure("macOS observer left an incomplete JSONL batch");
                return stopped;
            }

            for (var offset = 0; offset < count;)
            {
                var delimiter = Array.IndexOf(bytes, (byte)'\n', offset, count - offset);
                var length = (delimiter == -1 ? count : delimiter) - offset;
                if (line.Length + length >= maximumLine) throw ReadFailure("macOS observation batch exceeded its size limit");
                line.Write(bytes, offset, length);
                if (delimiter == -1) break;

                if (stopped) throw ReadFailure("macOS observer emitted a batch after stopping");
                JsonDocument document;
                try { document = JsonDocument.Parse(line.GetBuffer().AsMemory(0, (int)line.Length)); }
                catch (JsonException error) { writer.RecordReadFailure(error); throw; }
                using (document)
                {
                    var batch = document.RootElement;
                    long through;
                    try
                    {
                        through = batch.GetProperty("observedThrough").GetInt64();
                        stopped = batch.GetProperty("stopped").GetBoolean();
                    }
                    catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException)
                    {
                        writer.RecordReadFailure(error);
                        throw;
                    }
                    writer.Run((command, run) =>
                    {
                        observations.Write(command, run, batch.GetProperty("events"));
                        if (stopped) observations.Finish();
                        return 0;
                    }, observedThrough: through);
                }
                line.SetLength(0);
                offset = delimiter + 1;
            }
        }

        InvalidDataException ReadFailure(string message)
        {
            var error = new InvalidDataException(message);
            writer.RecordReadFailure(error);
            return error;
        }
    }
}
