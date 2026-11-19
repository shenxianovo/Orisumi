using System.Buffers.Binary;
using System.Text;
using System.Runtime.InteropServices;
using System.Text.Json;
using Orisumi.Platform;

namespace Orisumi.Collectors.Browser;

public static class ChromeNativeHost
{
    private const string Name = "com.orisumi.browser";
    private const int MaxIncomingBytes = 64 * 1024 * 1024;
    private const int MaxReplyBytes = 1024 * 1024;
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromMinutes(2);

    public static string Register(string store, string extensionId, string executable)
    {
        if (extensionId.Length != 32 || extensionId.Any(character => character is < 'a' or > 'p'))
            throw new ArgumentException("expected a 32-character Chrome extension ID");

        var location = new Store(store);
        var launcher = Path.Combine(location.State, "chrome-native-host");
        Directory.CreateDirectory(location.State);
        // Chrome does not inherit the shell's .NET environment.
        var dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet"));
        var command = File.Exists(dotnet)
            ? $"{ShellQuote(dotnet)} {ShellQuote(Path.ChangeExtension(executable, ".dll"))}"
            : ShellQuote(executable);
        File.WriteAllText(launcher, $"#!/bin/sh\nexec {command} --store {ShellQuote(location.Root)} browser native-host\n");
        File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var manifestDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "Google", "Chrome", "NativeMessagingHosts");
        Directory.CreateDirectory(manifestDirectory);
        var manifest = Path.Combine(manifestDirectory, Name + ".json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            name = Name,
            description = "Orisumi browser collector",
            path = launcher,
            type = "stdio",
            allowed_origins = new[] { $"chrome-extension://{extensionId}/" },
        }));
        return manifest;
    }

    public static async Task Run(string store)
    {
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        await Run(store, input, output, SnapshotInterval);
    }

    internal static async Task Run(string store, Stream input, Stream output, TimeSpan snapshotInterval)
    {
        using var cancellation = new CancellationTokenSource();
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
            var bytes = JsonSerializer.SerializeToUtf8Bytes(reply, JsonSerializerOptions.Web);
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
            var first = await Read();
            if (first is null) return;
            using (var hello = JsonDocument.Parse(first))
            {
                if (hello.RootElement.Text("type") != "open") throw new InvalidDataException("expected browser connection identity");
                database = new BrowserDatabase(store, hello.RootElement.Deserialize<BrowserDatabase.Hello>(JsonSerializerOptions.Web)!, Now());
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

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
}
