using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Orisumi.Raw;

namespace Orisumi.Capturing;

public sealed record ForegroundResult(int Observations, CaptureResult? LastResult, string? Failure);

public sealed class ForegroundCollector
{
    internal const string Format = "orisumi-foreground-v1";
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private readonly string _storeDirectory;
    private readonly string _collector;
    private readonly ProcessStartInfo _start;
    private readonly bool _requiresMac;

    public ForegroundCollector(string storeDirectory, string collector)
        : this(storeDirectory, collector, new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "orisumi-foreground")))
    {
        _requiresMac = true;
    }

    internal ForegroundCollector(string storeDirectory, string collector, ProcessStartInfo start)
    {
        _storeDirectory = storeDirectory;
        _collector = collector;
        _start = start;
    }

    public async Task<ForegroundResult> RunAsync(TimeSpan? duration = null, CancellationToken token = default)
    {
        if (duration is { } limit && (limit <= TimeSpan.Zero || limit.TotalMilliseconds > uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(duration), "观察时长必须大于零且不超过约 49 天");
        if (_requiresMac && !OperatingSystem.IsMacOS())
            return new ForegroundResult(0, null, "前台应用观测需要 macOS 的图形用户会话");
        if (token.IsCancellationRequested) return new ForegroundResult(0, null, null);

        _start.UseShellExecute = false;
        _start.RedirectStandardInput = true;
        _start.RedirectStandardOutput = true;
        _start.RedirectStandardError = true;
        using var process = new Process { StartInfo = _start };
        var state = new ObservationState();
        RawStore? store = null;
        Task read = Task.CompletedTask;
        Task<string> stderr = Task.FromResult("");
        var started = false;
        string? failure = null;
        try
        {
            store = new RawStore(_storeDirectory);
            started = process.Start();
            if (!started) throw new IOException("前台观察程序没有启动");
            stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            read = ReadAsync(process.StandardOutput.BaseStream, store, state);
            var exited = process.WaitForExitAsync(CancellationToken.None);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (duration is { } timeout) stop.CancelAfter(timeout);
            var requested = Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
            await Task.WhenAny(read, exited, requested).ConfigureAwait(false);
            var stopRequested = requested.IsCompleted;
            if (!stopRequested) failure = "前台观察程序在停止请求前结束";
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("stop").ConfigureAwait(false);
                process.StandardInput.Close();
            }
            await Task.WhenAll(read, exited, stderr).WaitAsync(StopTimeout, CancellationToken.None).ConfigureAwait(false);
            if (process.ExitCode != 0) failure = $"前台观察程序退出码为 {process.ExitCode}";
            else if (stopRequested && !state.Stopped) failure = "前台观察程序退出时没有留下正常结束事实";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or TimeoutException)
        {
            failure = error is TimeoutException ? "前台观察程序在停止请求后 5 秒内未结束，已强制停止" : error.Message;
        }
        finally
        {
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            if (started)
            {
                try
                {
                    await Task.WhenAll(read, stderr).WaitAsync(StopTimeout, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
                {
                    if (failure is null) failure = error.Message;
                    else if (!failure.Contains(error.Message, StringComparison.Ordinal)) failure += $"；{error.Message}";
                }
            }
        }

        if (failure is not null)
        {
            if (started && process.ExitCode != 0 && !failure.Contains($"退出码为 {process.ExitCode}", StringComparison.Ordinal))
                failure += $"；退出码为 {process.ExitCode}";
            if (stderr.IsCompletedSuccessfully && !string.IsNullOrWhiteSpace(stderr.Result))
                failure += $"；stderr：{stderr.Result.Trim()}";
            if (store is not null)
            {
                try
                {
                    var writer = store.BeginCapture(Start(state), CancellationToken.None);
                    if (state.Pending is { } pending)
                    {
                        using var bytes = new MemoryStream(pending, writable: false);
                        writer.Add(state.PendingPath, Start(state).Origin, bytes, "appkit-observation-jsonl");
                        state.LastResult = writer.Complete(new CaptureFailure(failure));
                        if (state.PendingPath == "observation.jsonl") state.Observations++;
                    }
                    else state.LastResult = writer.Fail(failure);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    failure += $"；失败记录也无法保存：{error.Message}";
                }
            }
        }
        return new ForegroundResult(state.Observations, state.LastResult, failure);
    }

    private async Task ReadAsync(Stream output, RawStore store, ObservationState state)
    {
        var buffer = new byte[4096];
        using var line = new MemoryStream();
        while (await output.ReadAsync(buffer).ConfigureAwait(false) is var count && count != 0)
        {
            var start = 0;
            for (var index = 0; index < count; index++)
            {
                if (buffer[index] != (byte)'\n') continue;
                line.Write(buffer, start, index - start + 1);
                state.Pending = line.ToArray();
                state.PendingPath = "invalid-observation.jsonl";
                Validate(state.Pending, state);
                state.PendingPath = "observation.jsonl";
                var writer = store.BeginCapture(Start(state));
                using var bytes = new MemoryStream(state.Pending, writable: false);
                writer.Add("observation.jsonl", Start(state).Origin, bytes, "appkit-observation-jsonl");
                state.LastResult = writer.Complete();
                state.Observations++;
                state.Pending = null;
                line.SetLength(0);
                start = index + 1;
            }
            line.Write(buffer, start, count - start);
        }
        if (line.Length == 0) return;
        state.Pending = line.ToArray();
        state.PendingPath = "incomplete-observation.jsonl";
        throw new IOException("前台观察程序输出以未完成的一行结束，已取得字节随失败记录保存");
    }

    private CaptureStart Start(ObservationState state) => new("foreground", _collector,
        $"helper: {_start.FileName}\nrun: {state.Run ?? "unknown"}\nsequence: {state.Sequence.ToString(CultureInfo.InvariantCulture)}");

    private static void Validate(byte[] bytes, ObservationState state)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var run = root.GetProperty("run").GetString();
            var sequence = root.GetProperty("sequence").GetInt64();
            var kind = root.GetProperty("kind").GetString();
            if (root.GetProperty("format").GetString() != Format || !Guid.TryParse(run, out _)
                || sequence != state.Sequence + 1 || (state.Run is not null && run != state.Run)
                || !root.GetProperty("observedAt").TryGetDateTimeOffset(out _)
                || kind is not ("started" or "snapshot" or "notification" or "stopped")
                || (state.Sequence == 0 && kind != "started") || (state.Sequence != 0 && kind == "started") || state.Stopped)
                throw new IOException("前台观察程序输出的格式、运行身份或顺序不符合协议");
            state.Run = run;
            state.Sequence = sequence;
            state.Stopped = kind == "stopped";
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new IOException($"前台观察程序输出的观察行无效：{error.Message}", error);
        }
    }

    private sealed class ObservationState
    {
        public int Observations { get; set; }
        public long Sequence { get; set; }
        public string? Run { get; set; }
        public bool Stopped { get; set; }
        public byte[]? Pending { get; set; }
        public string PendingPath { get; set; } = "invalid-observation.jsonl";
        public CaptureResult? LastResult { get; set; }
    }
}
