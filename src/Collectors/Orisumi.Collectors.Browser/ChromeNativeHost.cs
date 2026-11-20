using System.Buffers.Binary;
using System.Text;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Orisumi.Platform;

namespace Orisumi.Collectors.Browser;

public static class ChromeNativeHost
{
    private const int MaxIncomingBytes = 64 * 1024 * 1024;
    private const int MaxReplyBytes = 1024 * 1024;
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromMinutes(2);

    public static string Register(string store, string extensionId)
    {
        var instance = Instance.Open(store);
        var command = RuntimeFeature.IsDynamicCodeSupported
            ? new[] { RunProcess.Dotnet, Path.Combine(AppContext.BaseDirectory, "orisumi-browser.dll") }
            : [Path.Combine(AppContext.BaseDirectory, "orisumi-browser")];
        return ChromeExtension.Register(instance, extensionId,
            [.. command, "--store", instance.Store.Root, "native-host", "--registered"], SystemLocations.Current);
    }

    public static async Task Run(string store, bool registered, CancellationToken cancellation)
    {
        var location = new Store(store);
        string pidFile;
        using (Store.WaitLock(location.LockFile("chrome-registration")))
        {
            if (registered && !File.Exists(location.ChromeLauncher)) return;
            pidFile = Path.Combine(location.ChromeProcesses, $"{Environment.ProcessId}.pid");
            ProcessIdentity.Of(Environment.ProcessId)!.Write(pidFile);
        }
        try
        {
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            await Run(store, input, output, SnapshotInterval, cancellation);
        }
        finally { File.Delete(pidFile); }
    }

    internal static async Task Run(string store, Stream input, Stream output, TimeSpan snapshotInterval,
        CancellationToken stop = default)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var token = cancellation.Token;
        var header = new byte[sizeof(int)];
        var clock = TimeProvider.System;
        long Now() => clock.GetUtcNow().ToUnixTimeMilliseconds();

        async Task<byte[]?> Read()
        {
            if (await input.ReadAsync(header.AsMemory(0, 1), token) == 0) return null;
            await input.ReadExactlyAsync(header.AsMemory(1), token);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length is <= 0 or > MaxIncomingBytes) throw new InvalidDataException("invalid native message length");
            var bytes = new byte[length];
            await input.ReadExactlyAsync(bytes, token);
            return bytes;
        }
        async Task Write(BrowserDatabase.Reply reply)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(reply, BrowserJson.Default.Reply);
            if (bytes.Length > MaxReplyBytes) throw new InvalidDataException("native reply exceeds Chrome's message limit");
            var prefix = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
            await output.WriteAsync(prefix);
            await output.WriteAsync(bytes);
            await output.FlushAsync();
        }
        async Task Report(Exception error)
        {
            Console.Error.WriteLine("orisumi: " + error);
            await Write(new(false, Error: error.Message[..Math.Min(1024, error.Message.Length)]));
        }

        BrowserDatabase? database = null;
        Exception? failure = null;
        try
        {
            var first = await Read().WaitAsync(token);
            if (first is null) return;
            using (var hello = JsonDocument.Parse(first))
            {
                if (hello.RootElement.Text("type") != "open") throw new InvalidDataException("expected browser connection identity");
                database = new BrowserDatabase(store, hello.RootElement.Deserialize(BrowserJson.Default.Hello)!, Now());
            }
            await Write(new());
            var lastSnapshot = Now();
            using var watchdog = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Min(5000, snapshotInterval.TotalMilliseconds / 4)));
            var lastTick = clock.GetTimestamp();
            var tick = watchdog.WaitForNextTickAsync(token).AsTask();
            var read = Read();
            var timedOut = false;
            var parts = new StringBuilder();
            while (true)
            {
                await Task.WhenAny(read, tick);
                if (tick.IsCompleted)
                {
                    await tick;
                    if (!timedOut && clock.GetElapsedTime(lastTick) >= snapshotInterval)
                    {
                        database.SnapshotTimeout(lastSnapshot);
                        timedOut = true;
                    }
                    tick = watchdog.WaitForNextTickAsync(token).AsTask();
                }
                if (!read.IsCompleted) continue;
                var bytes = await read;
                if (bytes is null) break;
                using var frame = JsonDocument.Parse(bytes);
                var message = frame.RootElement;
                var type = message.Text("type");
                string? assembled = null;
                if (type == "part")
                {
                    parts.Append(message.Text("body") ?? throw new InvalidDataException("missing native message part"));
                    if (message.Flag("more") == true)
                    {
                        read = Read();
                        continue;
                    }
                    assembled = parts.ToString();
                    parts.Clear();
                    type = "delivery";
                }
                else if (parts.Length != 0) throw new InvalidDataException("interrupted native message parts");

                try
                {
                    if (type == "history_poll")
                        await Write(database.PollHistory(Now()));
                    else if (type == "delivery")
                    {
                        using var joined = assembled is null ? null : JsonDocument.Parse(assembled);
                        var payload = joined?.RootElement ?? message;
                        var reply = database.Receive(payload, Now(), out var polledTabs);
                        await Write(reply);
                        if (polledTabs)
                        {
                            lastTick = clock.GetTimestamp();
                            lastSnapshot = Now();
                            timedOut = false;
                        }
                    }
                    else throw new InvalidDataException("invalid native message type");
                }
                catch (Exception error) when (error is IOException or InvalidDataException or JsonException or InvalidOperationException
                    or KeyNotFoundException or FormatException or OverflowException)
                {
                    await Report(error);
                }
                read = Read();
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            failure = error;
            try { await Report(error); }
            catch (IOException) { }
        }
        finally
        {
            cancellation.Cancel();
            if (database is not null)
            {
                using (database) database.Stop(Now(), failure);
            }
        }
    }
}
