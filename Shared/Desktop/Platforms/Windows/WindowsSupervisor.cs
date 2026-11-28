using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Orisumi.Platform;

namespace Orisumi.Desktop;

internal static class WindowsSupervisor
{
    internal const string Executable = "orisumi-supervisor.exe";

    internal static int Run(string root)
    {
        var store = Instance.Open(root).Store;
        var files = new JobFiles(store);
        using var guard = files.Lock();
        Directory.CreateDirectory(files.DirectoryPath);
        using var changed = new AutoResetEvent(false);
        using var ending = new ManualResetEvent(false);
        using var stopped = new ManualResetEvent(false);
        using var window = new SessionWindow(() => { ending.Set(); stopped.WaitOne(); });
        using var group = new CollectorJobObject();
        using var watcher = new FileSystemWatcher(files.DirectoryPath, "*.json");
        watcher.Created += Wake;
        watcher.Changed += Wake;
        watcher.Deleted += Wake;
        watcher.Renamed += Wake;
        watcher.Error += (_, _) => changed.Set();
        watcher.EnableRaisingEvents = true;
        var jobs = new Dictionary<string, Child>();
        byte[]? published = null;
        try
        {
            Publish();
            do
            {
                var desired = files.Names().Select(name => (Name: name, Bytes: JobFiles.Read(files.Desired(name))))
                    .Where(item => item.Bytes is not null).ToDictionary(item => item.Name, item => item.Bytes!);
                foreach (var (name, child) in jobs.ToArray())
                {
                    if (desired.TryGetValue(name, out var bytes) && JobFiles.Hash(bytes) == child.Hash) continue;
                    child.Stop().GetAwaiter().GetResult();
                    jobs.Remove(name);
                    Publish();
                }
                if (desired.Count == 0) return 0;
                foreach (var (name, bytes) in desired)
                {
                    if (ending.WaitOne(0)) break;
                    if (!jobs.TryGetValue(name, out var child))
                    {
                        var spec = JsonSerializer.Deserialize(bytes, JobJson.Default.JobSpec)
                            ?? throw new InvalidDataException($"无效的任务：{name}");
                        jobs.Add(name, child = new(spec, JobFiles.Hash(bytes)));
                    }
                    child.Reconcile(group);
                    Publish();
                }
            } while (WaitHandle.WaitAny([ending, changed], TimeSpan.FromSeconds(2)) != 0);
            return 0;
        }
        finally
        {
            try
            {
                Task.WhenAll(jobs.Values.Select(job => job.Stop())).GetAwaiter().GetResult();
                jobs.Clear();
                Publish();
            }
            finally { stopped.Set(); }
        }

        void Wake(object sender, FileSystemEventArgs e) { if (e.FullPath != files.StatusPath) changed.Set(); }
        void Publish()
        {
            var status = jobs.ToDictionary(pair => pair.Key, pair => pair.Value.Status);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(status, JobJson.Default.DictionaryStringJobStatus);
            if (published is not null && bytes.AsSpan().SequenceEqual(published)) return;
            JobFiles.Write(files.StatusPath, bytes);
            published = bytes;
        }
    }

    private sealed class Child(JobSpec spec, string hash)
    {
        private Process? process;
        private int? lastExit;
        private long? started;
        private long attempt;
        internal string Hash => hash;
        internal JobStatus Status => new(process is null ? "waiting" : "running", process?.Id, lastExit, hash, started);

        internal void Reconcile(CollectorJobObject group)
        {
            if (process is { HasExited: true })
            {
                lastExit = process.ExitCode;
                process.Dispose();
                process = null;
            }
            if (process is not null || attempt != 0 && Stopwatch.GetElapsedTime(attempt) < TimeSpan.FromSeconds(10)) return;
            attempt = Stopwatch.GetTimestamp();
            try
            {
                process = group.Start(spec);
                started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(spec.StandardError)!);
                File.AppendAllText(spec.StandardError, $"{error.Message}{Environment.NewLine}");
            }
        }

        internal async Task Stop()
        {
            if (process is null) return;
            using (process)
            {
                var exited = process.WaitForExitAsync();
                await RunProcess.StopChild(process, exited);
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await exited;
            }
            process = null;
        }
    }
}
