using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orisumi.Core;

namespace Orisumi.Desktop;

internal sealed class WindowsCollectorJobs(Instance instance, ICaptureTask task) : ICollectorJobs
{
    private readonly JobFiles files = new(instance.Store);
    private readonly SemaphoreSlim edits = new(1);

    internal WindowsCollectorJobs(Instance instance) : this(instance, new CaptureTask(instance)) { }

    private sealed record Snapshot(string Name, byte[]? Desired, JobStatus? Status, string? TaskXml, bool HostAlive)
        : SavedJob(Name, Desired is not null, Status is not null, Status?.State ?? "not_loaded",
            Status?.Pid, Status?.LastExitCode is int code ? JsonValue.Create(code) : null);

    public Task<IReadOnlyDictionary<string, SavedJob>> Inspect(IEnumerable<string> sources, CancellationToken token)
    {
        var installed = ReleaseManifest.ReadInstalled(instance.Store)?.Components.Keys.AsEnumerable() ?? [];
        var names = files.Names().Union(installed.Where(name => name != "host" && Platforms.Runs(name, Platforms.CurrentRid))).Union(sources);
        var alive = files.Alive();
        var status = alive ? files.Status() : [];
        var xml = task.Query(token);
        return Task.FromResult<IReadOnlyDictionary<string, SavedJob>>(names.ToDictionary(name => name, SavedJob (name) =>
            new Snapshot(name, JobFiles.Read(files.Desired(name)), status.GetValueOrDefault(name), xml, alive)));
    }

    public async Task<string> Set(string source, JobSpec? desired, CancellationToken token)
    {
        await edits.WaitAsync(token);
        try
        {
            var bytes = desired is null ? null : JsonSerializer.SerializeToUtf8Bytes(desired, JobJson.Default.JobSpec);
            await Apply(source, bytes, desired?.StandardError, null, token);
            return task.Name + " (" + source + ")";
        }
        finally { edits.Release(); }
    }

    public async Task Restore(SavedJob previous, CancellationToken token)
    {
        var saved = (Snapshot)previous;
        await edits.WaitAsync(token);
        try { await Apply(saved.Source, saved.Desired, null, saved, token); }
        finally { edits.Release(); }
    }

    private async Task Apply(string source, byte[]? bytes, string? errorPath, Snapshot? restore, CancellationToken token)
    {
        JobFiles.Write(files.Desired(source), bytes);
        var registered = task.Query(token);
        var any = files.Names().Any();
        var register = restore is null ? any : restore.TaskXml is not null;
        if (register && registered is null) task.Register(restore?.TaskXml, token);
        var start = restore is null ? bytes is not null : restore.Supervised;
        if (start && !files.Alive())
        {
            try { task.Start(token); }
            catch (Exception error) when (error is IOException or COMException) { throw new IOException($"{error.Message} 运行此任务需要当前用户已登录 Windows 桌面。", error); }
        }
        // Undo a task start that was part of a failed installation.
        if (any && restore is { HostAlive: false } && files.Alive()) task.End(token);

        var expected = bytes is null ? null : JobFiles.Hash(bytes);
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var alive = files.Alive();
            var actual = alive ? files.Status().GetValueOrDefault(source) : null;
            var settled = expected is null ? actual is null : actual is { State: "running" } && actual.Hash == expected;
            if (!any || restore is { HostAlive: false }) settled = !alive;
            if (settled) break;
            if (elapsed.Elapsed >= RunProcess.ChildStopTimeout + TimeSpan.FromSeconds(15))
            {
                errorPath ??= Path.Combine(instance.Store.Logs, source + ".err.log");
                var hint = start && !alive ? "任务需要当前用户已登录 Windows 桌面；请检查登录会话和任务的上次运行结果。" : "";
                throw new IOException($"{source}: 等待托管状态超时。{hint}{Environment.NewLine}{LogTail(errorPath)}");
            }
            await Task.Delay(100, token);
        }
        if (!register) task.Delete(token);
    }

    private static string LogTail(string path)
    {
        if (!File.Exists(path)) return "";
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        file.Position = Math.Max(0, file.Length - 8192);
        using var reader = new StreamReader(file);
        return string.Join(Environment.NewLine, reader.ReadToEnd().TrimEnd().Split('\n').TakeLast(6));
    }
}
