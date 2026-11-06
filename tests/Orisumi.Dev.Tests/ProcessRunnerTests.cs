using System.Diagnostics;

namespace Orisumi.Dev.Tests;

public class ProcessRunnerTests
{
    [Test]
    public async Task CancellationAllowsTheProductToFinishBeforeTheRunnerExits()
    {
        if (OperatingSystem.IsWindows()) return;
        using var sample = new ProcessSample();
        var task = sample.Run("printf ready > ready; while [ ! -f stop ]; do sleep 0.02; done; sleep 0.1; printf tail; printf finished > finished", TimeSpan.FromSeconds(2));
        await sample.WaitFor("ready");
        sample.Cancel();
        File.WriteAllText(sample.PathFor("stop"), "stop");
        await Assert.That(async () => { await task; }).Throws<OperationCanceledException>();
        await Assert.That(File.Exists(sample.PathFor("finished"))).IsTrue();
        await Assert.That(sample.Output.ToString().Contains("tail", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task CancellationStillKillsAProductThatDoesNotExitWithinTheGracePeriod()
    {
        if (OperatingSystem.IsWindows()) return;
        using var sample = new ProcessSample();
        var task = sample.Run("printf '%s' \"$$\" > pid; printf ready > ready; sleep 30", TimeSpan.FromMilliseconds(100));
        await sample.WaitFor("ready");
        using var process = Process.GetProcessById(int.Parse(File.ReadAllText(sample.PathFor("pid")), System.Globalization.CultureInfo.InvariantCulture));
        sample.Cancel();
        await Assert.That(async () => { await task.WaitAsync(TimeSpan.FromSeconds(5)); }).Throws<OperationCanceledException>();
        await Assert.That(process.HasExited).IsTrue();
    }

    private sealed class ProcessSample : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "orisumi-process-tests", Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource _stop = new();

        public ProcessSample() => Directory.CreateDirectory(_directory);

        public StringWriter Output { get; } = new();

        public string PathFor(string name) => Path.Combine(_directory, name);

        public Task<int> Run(string script, TimeSpan grace) => new ProcessRunner(_directory).StreamAsync(
            new ProcessCommand("/bin/sh", ["-c", script], CancellationGracePeriod: grace), Output, _stop.Token);

        public void Cancel() => _stop.Cancel();

        public async Task WaitFor(string name)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(PathFor(name))) await Task.Delay(10, timeout.Token);
        }

        public void Dispose()
        {
            _stop.Cancel();
            _stop.Dispose();
            Output.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
