using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Orisumi.Raw;

namespace Orisumi.Capturing.Tests;

public class ForegroundCollectorTests
{
    [Test]
    public async Task NativeHelperSurvivesTerminalInterruptUntilItsParentRequestsStop()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var helper = new Process
        {
            StartInfo = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "orisumi-foreground"))
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        helper.Start();
        try
        {
            await helper.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await helper.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var start = new ProcessStartInfo("/bin/kill");
            start.ArgumentList.Add("-INT");
            start.ArgumentList.Add(helper.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var interrupt = Process.Start(start)!;
            await interrupt.WaitForExitAsync();
            await Task.Delay(100);
            await Assert.That(helper.HasExited).IsFalse();
            await helper.StandardInput.WriteLineAsync("stop");
            helper.StandardInput.Close();
            var remaining = await helper.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using var last = JsonDocument.Parse(remaining.Trim().Split('\n')[^1]);
            await Assert.That(last.RootElement.GetProperty("kind").GetString()).IsEqualTo("stopped");
            await Assert.That(last.RootElement.GetProperty("reason").GetString()).IsEqualTo("stop-requested");
            await Assert.That(helper.ExitCode).IsEqualTo(0);
        }
        finally
        {
            if (!helper.HasExited)
            {
                helper.Kill(entireProcessTree: true);
                await helper.WaitForExitAsync();
            }
        }
    }

    [Test]
    public async Task CompletePrefixIsReadableBeforeStopAndCancellationDrainsTheFinalObservation()
    {
        if (OperatingSystem.IsWindows()) return;
        using var sample = new ForegroundSample();
        var first = sample.Line(1, "started", "原样保留 空格") + "  \n";
        var last = sample.Line(2, "stopped") + "\n";
        var task = sample.Run($$"""
            printf '%s' {{Quote(first)}}
            IFS= read -r command
            printf '%s' {{Quote(last)}}
            sleep 0.1
            printf finished > {{Quote(sample.Finished)}}
            """);
        await sample.WaitForCaptures(1);
        await Assert.That(task.IsCompleted).IsFalse();
        await Assert.That(sample.Read(sample.Store.Captures().Single(), "observation.jsonl")).IsEqualTo(first);
        sample.Stop();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(result.Failure).IsNull();
        await Assert.That(result.Observations).IsEqualTo(2);
        await Assert.That(sample.Read(result.LastResult!.Capture, "observation.jsonl")).IsEqualTo(last);
        await Assert.That(File.ReadAllText(sample.Finished)).IsEqualTo("finished");
    }

    [Test]
    public async Task NonzeroExitPreservesThePrefixAndRecordsStderr()
    {
        if (OperatingSystem.IsWindows()) return;
        using var sample = new ForegroundSample();
        var first = sample.Line(1, "started") + "\n";
        var result = await sample.Run($$"""
            printf '%s' {{Quote(first)}}
            printf 'workspace failed\n' >&2
            exit 7
            """).WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(result.Observations).IsEqualTo(1);
        await Assert.That(result.Failure!.Contains('7')).IsTrue();
        await Assert.That(result.Failure.Contains("workspace failed", StringComparison.Ordinal)).IsTrue();
        await Assert.That(result.LastResult!.Capture.Record.Failure).IsNotNull();
        var saved = sample.Store.Captures().Single(capture => capture.Record.Failure is null);
        await Assert.That(sample.Read(saved, "observation.jsonl")).IsEqualTo(first);
    }

    [Test]
    public async Task IncompleteLastLineSurvivesWithTheFailureCapture()
    {
        if (OperatingSystem.IsWindows()) return;
        using var sample = new ForegroundSample();
        const string partial = "{\"partial\":\"未结束";
        var result = await sample.Run($$"""
            printf '%s\n' {{Quote(sample.Line(1, "started"))}}
            printf '%s' {{Quote(partial)}}
            printf 'unexpected EOF\n' >&2
            exec 0<&-
            exec 1>&-
            sleep 0.1
            exit 9
            """).WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(result.Observations).IsEqualTo(1);
        await Assert.That(result.Failure!.Contains("未完成", StringComparison.Ordinal)).IsTrue();
        await Assert.That(result.Failure.Contains("unexpected EOF", StringComparison.Ordinal)).IsTrue();
        await Assert.That(sample.Read(result.LastResult!.Capture, "incomplete-observation.jsonl")).IsEqualTo(partial);
    }

    [Test]
    public async Task WrongSequenceIsSavedWithFailureInsteadOfBecomingAnObservation()
    {
        if (OperatingSystem.IsWindows()) return;
        using var sample = new ForegroundSample();
        var invalid = sample.Line(3, "snapshot") + "\n";
        var result = await sample.Run($$"""
            printf '%s\n' {{Quote(sample.Line(1, "started"))}}
            printf '%s' {{Quote(invalid)}}
            IFS= read -r command
            """).WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(result.Observations).IsEqualTo(1);
        await Assert.That(result.Failure!.Contains("顺序", StringComparison.Ordinal)).IsTrue();
        await Assert.That(sample.Read(result.LastResult!.Capture, "invalid-observation.jsonl")).IsEqualTo(invalid);
    }

    [Test]
    public async Task DurationRequiresAStopObservationEvenWhenTheProcessExitsSuccessfully()
    {
        if (OperatingSystem.IsWindows()) return;
        using var sample = new ForegroundSample();
        var result = await sample.Run($$"""
            printf '%s\n' {{Quote(sample.Line(1, "started"))}}
            IFS= read -r command
            """, TimeSpan.FromMilliseconds(100)).WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(result.Failure!.Contains("正常结束事实", StringComparison.Ordinal)).IsTrue();
        await Assert.That(result.Observations).IsEqualTo(1);
    }

    [Test]
    public async Task ProcessThatIgnoresStopIsKilledAndItsPrefixRemainsReadable()
    {
        if (OperatingSystem.IsWindows()) return;
        using var sample = new ForegroundSample();
        var task = sample.Run($$"""
            printf '%s' "$$" > {{Quote(sample.Pid)}}
            printf '%s\n' {{Quote(sample.Line(1, "started"))}}
            IFS= read -r command
            sleep 30
            """);
        await sample.WaitForCaptures(1);
        var pid = int.Parse(File.ReadAllText(sample.Pid), System.Globalization.CultureInfo.InvariantCulture);
        using var process = Process.GetProcessById(pid);
        sample.Stop();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(15));
        await Assert.That(result.Failure!.Contains("强制停止", StringComparison.Ordinal)).IsTrue();
        await Assert.That(result.Observations).IsEqualTo(1);
        await Assert.That(process.HasExited).IsTrue();
    }

    [Test]
    public async Task StorageFailureStopsTheHelperAndReportsTheLastSavedPrefix()
    {
        if (OperatingSystem.IsWindows()) return;
        using var sample = new ForegroundSample();
        var first = sample.Line(1, "started") + "\n";
        var task = sample.Run($$"""
            printf '%s' {{Quote(first)}}
            while [ ! -e {{Quote(sample.Gate)}} ]; do sleep 0.02; done
            printf '%s\n' {{Quote(sample.Line(2, "snapshot"))}}
            IFS= read -r command
            printf '%s\n' {{Quote(sample.Line(3, "stopped"))}}
            printf finished > {{Quote(sample.Finished)}}
            """);
        await sample.WaitForCaptures(1);
        var records = Path.Combine(sample.StoreDirectory, "records");
        var moved = records + "-saved";
        Directory.Move(records, moved);
        File.WriteAllText(records, "blocked");
        try
        {
            File.WriteAllText(sample.Gate, "next");
            var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(result.Observations).IsEqualTo(1);
            await Assert.That(result.Failure!.Contains("失败记录也无法保存", StringComparison.Ordinal)).IsTrue();
            await Assert.That(File.ReadAllText(sample.Finished)).IsEqualTo("finished");
            await Assert.That(sample.Read(result.LastResult!.Capture, "observation.jsonl")).IsEqualTo(first);
        }
        finally
        {
            File.Delete(records);
            Directory.Move(moved, records);
        }
    }

    [Test]
    public async Task ValidObservationKeepsItsPathWhenFailureRecordingBecomesWritableAgain()
    {
        if (OperatingSystem.IsWindows()) return;
        using var sample = new ForegroundSample();
        var second = sample.Line(2, "snapshot") + "\n";
        var records = Path.Combine(sample.StoreDirectory, "records");
        var moved = records + "-saved";
        var task = sample.Run($$"""
            printf '%s\n' {{Quote(sample.Line(1, "started"))}}
            while [ ! -e {{Quote(sample.Gate)}} ]; do sleep 0.02; done
            printf '%s' {{Quote(second)}}
            IFS= read -r command
            rm {{Quote(records)}}
            mv {{Quote(moved)}} {{Quote(records)}}
            """);
        await sample.WaitForCaptures(1);
        Directory.Move(records, moved);
        File.WriteAllText(records, "blocked");
        try
        {
            File.WriteAllText(sample.Gate, "next");
            var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(result.Observations).IsEqualTo(2);
            await Assert.That(result.Failure).IsNotNull();
            await Assert.That(result.LastResult!.Capture.Record.Failure).IsNotNull();
            await Assert.That(sample.Read(result.LastResult.Capture, "observation.jsonl")).IsEqualTo(second);
        }
        finally
        {
            if (File.Exists(records)) File.Delete(records);
            if (Directory.Exists(moved)) Directory.Move(moved, records);
        }
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private sealed class ForegroundSample : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "orisumi-foreground-" + Guid.NewGuid().ToString("N"));
        private readonly string _run = Guid.NewGuid().ToString();
        private readonly CancellationTokenSource _stop = new();
        private Task<ForegroundResult>? _task;

        public ForegroundSample()
        {
            Directory.CreateDirectory(_directory);
            Store = new RawStore(StoreDirectory);
        }

        public RawStore Store { get; }
        public string StoreDirectory => Path.Combine(_directory, "store");
        public string Finished => Path.Combine(_directory, "finished");
        public string Pid => Path.Combine(_directory, "pid");
        public string Gate => Path.Combine(_directory, "gate");

        public string Line(int sequence, string kind, string? detail = null) => JsonSerializer.Serialize(new
        {
            format = ForegroundCollector.Format,
            run = _run,
            sequence,
            observedAt = "2026-11-06T12:00:00.000Z",
            kind,
            detail,
        });

        public Task<ForegroundResult> Run(string script, TimeSpan? duration = null)
        {
            var start = new ProcessStartInfo("/bin/sh");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(script);
            _task = new ForegroundCollector(StoreDirectory, "tests", start).RunAsync(duration, _stop.Token);
            return _task;
        }

        public void Stop() => _stop.Cancel();

        public string Read(Capture capture, string path)
        {
            using var reader = new StreamReader(Store.Open(capture, path), Encoding.UTF8);
            return reader.ReadToEnd();
        }

        public async Task WaitForCaptures(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (Store.Captures().Count < count) await Task.Delay(20, timeout.Token);
        }

        public void Dispose()
        {
            _stop.Cancel();
            _task?.GetAwaiter().GetResult();
            _stop.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
