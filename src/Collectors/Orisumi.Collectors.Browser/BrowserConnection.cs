using System.Net.WebSockets;
using System.Text.Json;

namespace Orisumi.Collectors.Browser;

internal static class BrowserConnection
{
    private static readonly ReadOnlyMemory<byte> Heartbeat = """{"type":"heartbeat"}"""u8.ToArray();

    internal static async Task Run(string store, Func<Task<ReadOnlyMemory<byte>?>> receive, Func<ReadOnlyMemory<byte>, Task> send,
        TimeSpan snapshotInterval, TimeSpan heartbeatInterval, CancellationToken stop)
    {
        var clock = TimeProvider.System;
        long Now() => clock.GetUtcNow().ToUnixTimeMilliseconds();
        Task Write(BrowserDatabase.Reply reply) => send(JsonSerializer.SerializeToUtf8Bytes(reply, BrowserJson.Default.Reply));

        async Task Report(Exception error)
        {
            Console.Error.WriteLine("orisumi: " + error);
            await Write(new(false, Error: error.Message[..Math.Min(1024, error.Message.Length)]));
        }

        BrowserDatabase? database = null;
        Exception? failure = null;
        try
        {
            using var watchdog = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Min(5000, snapshotInterval.TotalMilliseconds / 4)));
            using var heartbeat = new PeriodicTimer(heartbeatInterval);
            var tick = watchdog.WaitForNextTickAsync().AsTask();
            var beat = heartbeat.WaitForNextTickAsync().AsTask();
            var read = receive();
            var lastSnapshot = Now();
            var lastTick = clock.GetTimestamp();
            var timedOut = false;
            while (true)
            {
                await Task.WhenAny(read, tick, beat).WaitAsync(stop);
                stop.ThrowIfCancellationRequested();
                if (beat.IsCompleted)
                {
                    await send(Heartbeat);
                    beat = heartbeat.WaitForNextTickAsync().AsTask();
                }
                if (tick.IsCompleted)
                {
                    if (database is not null && !timedOut && clock.GetElapsedTime(lastTick) >= snapshotInterval)
                    {
                        database.SnapshotTimeout(lastSnapshot);
                        timedOut = true;
                    }
                    tick = watchdog.WaitForNextTickAsync().AsTask();
                }
                if (!read.IsCompleted) continue;
                var bytes = await read;
                if (bytes is null) break;
                using var frame = JsonDocument.Parse(bytes.Value);
                var message = frame.RootElement;
                var type = message.Text("type");
                if (database is null)
                {
                    if (type != "open") throw new InvalidDataException("expected browser connection identity");
                    database = new BrowserDatabase(store, message.Deserialize(BrowserJson.Default.Hello)!, Now());
                    await Write(new());
                    lastTick = clock.GetTimestamp();
                    lastSnapshot = Now();
                }
                else
                {
                    try
                    {
                        if (type == "history_poll")
                            await Write(database.PollHistory(Now()));
                        else if (type == "delivery")
                        {
                            var reply = database.Receive(message, Now(), out var polledTabs);
                            await Write(reply);
                            if (polledTabs)
                            {
                                lastTick = clock.GetTimestamp();
                                lastSnapshot = Now();
                                timedOut = false;
                            }
                        }
                        else throw new InvalidDataException("invalid browser message type");
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException or JsonException or InvalidOperationException
                        or KeyNotFoundException or FormatException or OverflowException)
                    {
                        await Report(error);
                    }
                }
                read = receive();
            }
        }
        catch (Exception error) when (stop.IsCancellationRequested || error is WebSocketException) { }
        catch (Exception error)
        {
            failure = error;
            try { await Report(error); }
            catch (Exception transport) when (transport is IOException or WebSocketException or OperationCanceledException) { }
        }
        finally
        {
            if (database is not null)
            {
                using (database) database.Stop(Now(), failure);
            }
        }
    }
}
