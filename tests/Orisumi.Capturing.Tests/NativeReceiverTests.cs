using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orisumi.Raw;

namespace Orisumi.Capturing.Tests;

public class NativeReceiverTests
{
    private const string Instance = "10203040-5060-4070-8090-a0b0c0d0e0f0";
    private const string Run = "f0e0d0c0-b0a0-4090-8070-605040302010";
    private const string Caller = "chrome-extension://abcdefghijklmnopabcdefghijklmnop/";

    [Test]
    public async Task PayloadBytesAreRestorableAndTheReceiptNamesTheirCapture()
    {
        using var sample = new CaptureSample();
        var payload = Observation(42);
        using var input = new MemoryStream(Frame(payload));
        using var output = new MemoryStream();
        await new NativeReceiver(sample.StoreDirectory, "tests").RunAsync(input, output, Caller);
        var capture = sample.Store.Captures().Single();
        await Assert.That(Read(sample.Store, capture, "observation.json").SequenceEqual(payload)).IsTrue();
        await Assert.That(capture.Source).IsEqualTo("browser");
        await Assert.That(capture.Record.Failure).IsNull();
        await Assert.That(sample.Store.Manifest(capture).Single().Method).IsEqualTo("chrome-native-message-json");
        var receipt = Receipts(output).Single();
        await Assert.That(receipt.GetProperty("format").GetString()).IsEqualTo("orisumi-browser-ack-v1");
        await Assert.That(receipt.GetProperty("instance").GetString()).IsEqualTo(Instance);
        await Assert.That(receipt.GetProperty("run").GetString()).IsEqualTo(Run);
        await Assert.That(receipt.GetProperty("sequence").GetInt64()).IsEqualTo(42);
        await Assert.That(receipt.GetProperty("capture").GetString()).IsEqualTo(capture.Name.ToString());
    }

    [Test]
    public async Task InvalidJsonIsSavedWithFailureWithoutAReceipt()
    {
        using var sample = new CaptureSample();
        byte[] payload = Encoding.UTF8.GetBytes("{ \"unfinished\": \"中文");
        using var input = new MemoryStream(Frame(payload));
        using var output = new MemoryStream();
        await Assert.That(() => new NativeReceiver(sample.StoreDirectory, "tests").RunAsync(input, output, Caller)).Throws<IOException>();
        var capture = sample.Store.Captures().Single();
        await Assert.That(capture.Record.Failure).IsNotNull();
        await Assert.That(Read(sample.Store, capture, "invalid-observation.json").SequenceEqual(payload)).IsTrue();
        await Assert.That(capture.Record.Origin.Contains("frame: 1", StringComparison.Ordinal)).IsTrue();
        await Assert.That(capture.Record.Origin.Contains("instance:", StringComparison.Ordinal)).IsFalse();
        await Assert.That(output.Length).IsEqualTo(0);
    }

    [Test]
    [Arguments("header")]
    [Arguments("payload")]
    [Arguments("oversized")]
    public async Task BrokenFramingPreservesExactlyTheBytesAlreadyRead(string kind)
    {
        using var sample = new CaptureSample();
        var received = kind switch
        {
            "header" => new byte[] { 0x20, 0x00, 0x00 },
            "payload" => Frame(Observation(8))[..^7],
            _ => BitConverter.GetBytes(64u * 1024 * 1024 + 1),
        };
        using var input = new MemoryStream(kind == "oversized" ? [.. received, .. Observation(9)] : received);
        using var output = new MemoryStream();
        await Assert.That(() => new NativeReceiver(sample.StoreDirectory, "tests").RunAsync(input, output, Caller)).Throws<IOException>();
        var capture = sample.Store.Captures().Single();
        var path = kind == "oversized" ? "invalid-frame.bin" : "incomplete-frame.bin";
        await Assert.That(Read(sample.Store, capture, path).SequenceEqual(received)).IsTrue();
        await Assert.That(sample.Store.Manifest(capture).Single().Method).IsEqualTo("chrome-native-message-frame");
        await Assert.That(capture.Record.Failure).IsNotNull();
        await Assert.That(output.Length).IsEqualTo(0);
        await Assert.That(input.Position).IsEqualTo(received.Length);
    }

    [Test]
    [Arguments("format", "\"another-format\"")]
    [Arguments("instance", "\"unknown\"")]
    [Arguments("run", "null")]
    [Arguments("sequence", "0")]
    [Arguments("sequence", "-1")]
    [Arguments("sequence", "1.5")]
    [Arguments("sequence", "9007199254740992")]
    [Arguments("observedAt", "\"yesterday\"")]
    [Arguments("kind", "\"event\"")]
    public async Task InvalidEnvelopeIsPreservedAsFailure(string field, string value)
    {
        using var sample = new CaptureSample();
        var observation = JsonNode.Parse(Observation(42))!.AsObject();
        observation[field] = JsonNode.Parse(value);
        var payload = Encoding.UTF8.GetBytes(observation.ToJsonString());
        using var input = new MemoryStream(Frame(payload));
        using var output = new MemoryStream();
        await Assert.That(() => new NativeReceiver(sample.StoreDirectory, "tests").RunAsync(input, output, Caller)).Throws<IOException>();
        var capture = sample.Store.Captures().Single();
        await Assert.That(capture.Record.Failure).IsNotNull();
        await Assert.That(Read(sample.Store, capture, "invalid-observation.json").SequenceEqual(payload)).IsTrue();
        await Assert.That(output.Length).IsEqualTo(0);
    }

    [Test]
    public async Task StorageFailureRecordsItsReasonAndNeverAcknowledges()
    {
        using var sample = new CaptureSample();
        var store = sample.Store;
        File.WriteAllText(Path.Combine(sample.StoreDirectory, "blocks"), "blocked");
        using var input = new MemoryStream(Frame(Observation(1)));
        using var output = new MemoryStream();
        await Assert.That(() => new NativeReceiver(sample.StoreDirectory, "tests").RunAsync(input, output, Caller)).Throws<IOException>();
        var capture = store.Captures().Single();
        await Assert.That(capture.Record.Manifest).IsNull();
        await Assert.That(capture.Record.Failure!.Reason.Contains("原件存储", StringComparison.Ordinal)).IsTrue();
        await Assert.That(output.Length).IsEqualTo(0);
    }

    [Test]
    public async Task FragmentedFramesAndReplayedRunsKeepTheirBytesAndDeliveryOrder()
    {
        using var sample = new CaptureSample();
        var payloads = new[] { Observation(42), Observation(42), Observation(9, Guid.NewGuid().ToString()) };
        using var input = new FragmentedInput([.. payloads.SelectMany(Frame)]);
        using var output = new MemoryStream();
        await new NativeReceiver(sample.StoreDirectory, "tests").RunAsync(input, output, Caller);
        var receipts = Receipts(output);
        await Assert.That(receipts.Count).IsEqualTo(3);
        await Assert.That(sample.Store.Captures().Count).IsEqualTo(3);
        for (var index = 0; index < receipts.Count; index++)
        {
            var capture = sample.Store.Find(receipts[index].GetProperty("capture").GetString()!);
            await Assert.That(Read(sample.Store, capture, "observation.json").SequenceEqual(payloads[index])).IsTrue();
            await Assert.That(capture.Record.Origin.Contains($"frame: {index + 1}\n", StringComparison.Ordinal)).IsTrue();
        }
        using var replay = new MemoryStream(Frame(payloads[0]));
        using var replayOutput = new MemoryStream();
        await new NativeReceiver(sample.StoreDirectory, "tests").RunAsync(replay, replayOutput, Caller);
        await Assert.That(sample.Store.Captures().Count).IsEqualTo(4);
        await Assert.That(Receipts(replayOutput).Single().GetProperty("capture").GetString())
            .IsNotEqualTo(receipts[0].GetProperty("capture").GetString());
    }

    [Test]
    public async Task ReceiptStartsOnlyAfterTheCaptureCanBeRestored()
    {
        using var sample = new CaptureSample();
        var payload = Observation(1);
        using var input = new MemoryStream(Frame(payload));
        using var output = new CheckingOutput(() =>
        {
            var capture = sample.Store.Captures().Single();
            if (!Read(sample.Store, capture, "observation.json").SequenceEqual(payload)) throw new IOException("回执早于原件完成");
        });
        await new NativeReceiver(sample.StoreDirectory, "tests").RunAsync(input, output, Caller);
        await Assert.That(Receipts(output).Count).IsEqualTo(1);
    }

    [Test]
    public async Task BrokenReceiptDoesNotRepeatOrInvalidateTheCommittedObservation()
    {
        using var sample = new CaptureSample();
        var payload = Observation(1);
        using var input = new MemoryStream(Frame(payload));
        using var output = new CheckingOutput(() => throw new IOException("回执连接关闭"));
        await Assert.That(() => new NativeReceiver(sample.StoreDirectory, "tests").RunAsync(input, output, Caller)).Throws<IOException>();
        var capture = sample.Store.Captures().Single();
        await Assert.That(capture.Record.Failure).IsNull();
        await Assert.That(Read(sample.Store, capture, "observation.json").SequenceEqual(payload)).IsTrue();
    }

    [Test]
    public async Task CancellingDuringAFramePreservesTheReceivedPrefix()
    {
        using var sample = new CaptureSample();
        using var stop = new CancellationTokenSource();
        var frame = Frame(Observation(1));
        using var input = new InterruptedInput(frame, 7, stop.Cancel);
        using var output = new MemoryStream();
        await Assert.That(() => new NativeReceiver(sample.StoreDirectory, "tests").RunAsync(input, output, Caller, stop.Token))
            .Throws<OperationCanceledException>();
        var capture = sample.Store.Captures().Single();
        await Assert.That(Read(sample.Store, capture, "incomplete-frame.bin").AsSpan().SequenceEqual(frame.AsSpan(0, 7))).IsTrue();
        await Assert.That(capture.Record.Failure).IsNotNull();
        await Assert.That(output.Length).IsEqualTo(0);
    }

    [Test]
    public async Task InvalidUtf8InAnUnknownFieldRemainsARecordedFailure()
    {
        using var sample = new CaptureSample();
        var payload = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Observation(1)).Replace("\"unknown\":null", "\"unknown\":\"BAD\"", StringComparison.Ordinal));
        payload[Array.IndexOf(payload, (byte)'B')] = 0xFF;
        using var input = new MemoryStream(Frame(payload));
        using var output = new MemoryStream();
        await Assert.That(() => new NativeReceiver(sample.StoreDirectory, "tests").RunAsync(input, output, Caller)).Throws<IOException>();
        var capture = sample.Store.Captures().Single();
        await Assert.That(Read(sample.Store, capture, "invalid-observation.json").SequenceEqual(payload)).IsTrue();
        await Assert.That(output.Length).IsEqualTo(0);
    }

    [Test]
    public async Task InvalidFrameKeepsTheCommittedPrefixAndDoesNotReadTheFollowingFrame()
    {
        using var sample = new CaptureSample();
        var first = Frame(Observation(4));
        var invalid = Frame("{}"u8.ToArray());
        using var input = new MemoryStream([.. first, .. invalid, .. Frame(Observation(5))]);
        using var output = new MemoryStream();
        await Assert.That(() => new NativeReceiver(sample.StoreDirectory, "tests").RunAsync(input, output, Caller)).Throws<IOException>();
        var saved = sample.Store.Find(Receipts(output).Single().GetProperty("capture").GetString()!);
        await Assert.That(Read(sample.Store, saved, "observation.json").SequenceEqual(Observation(4))).IsTrue();
        var failed = sample.Store.Captures().Single(capture => capture.Record.Failure is not null);
        await Assert.That(failed.Record.Origin.Contains("frame: 2", StringComparison.Ordinal)).IsTrue();
        await Assert.That(failed.Record.Origin.Contains("run:", StringComparison.Ordinal)).IsFalse();
        await Assert.That(input.Position).IsEqualTo(first.Length + invalid.Length);
    }

    [Test]
    public async Task UnavailableRecordStorageKeepsOnlyThePreviouslyAcknowledgedCapture()
    {
        using var sample = new CaptureSample();
        var records = Path.Combine(sample.StoreDirectory, "records");
        var preserved = records + "-preserved";
        using var input = new MemoryStream([.. Frame(Observation(1)), .. Frame(Observation(2))]);
        var writes = 0;
        using var output = new CheckingOutput(() =>
        {
            if (++writes != 2) return;
            Directory.Move(records, preserved);
            File.WriteAllText(records, "blocked");
        });
        try
        {
            await Assert.That(() => new NativeReceiver(sample.StoreDirectory, "tests").RunAsync(input, output, Caller)).Throws<IOException>();
        }
        finally
        {
            if (File.Exists(records)) File.Delete(records);
            if (Directory.Exists(preserved)) Directory.Move(preserved, records);
        }
        var capture = sample.Store.Captures().Single();
        await Assert.That(Receipts(output).Single().GetProperty("capture").GetString()).IsEqualTo(capture.Name.ToString());
        await Assert.That(Read(sample.Store, capture, "observation.json").SequenceEqual(Observation(1))).IsTrue();
    }

    [Test]
    public async Task EmptyConnectionAndCancellationBeforeReceivingDoNotCreateObservations()
    {
        using var sample = new CaptureSample();
        using var output = new MemoryStream();
        await new NativeReceiver(sample.StoreDirectory, "tests").RunAsync(Stream.Null, output, Caller);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        using var input = new MemoryStream(Frame(Observation(1)));
        await Assert.That(() => new NativeReceiver(sample.StoreDirectory, "tests").RunAsync(input, output, Caller, stop.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(sample.Store.Captures().Count).IsEqualTo(0);
        await Assert.That(output.Length).IsEqualTo(0);
    }

    [Test]
    public async Task LauncherExecutesItsNeighborFromAPathContainingSpaces()
    {
        if (OperatingSystem.IsWindows()) return;
        using var sample = new CaptureSample();
        var directory = Path.Combine(sample.SourceDirectory, "published host with spaces");
        Directory.CreateDirectory(directory);
        var launcher = Path.Combine(directory, "orisumi-browser-host");
        File.Copy(LauncherSource(), launcher);
        await Assert.That((File.GetUnixFileMode(launcher) & UnixFileMode.UserExecute) != 0).IsTrue();
        var cli = Path.Combine(directory, "orisumi-cli");
        File.WriteAllText(cli, "#!/bin/sh\nprintf '%s\\n' \"$@\"\n");
        File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var start = new ProcessStartInfo(launcher) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Caller);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(await output).IsEqualTo($"capture\nbrowser\n--\n{Caller}\n");
        await Assert.That(await error).IsEqualTo("");
        await Assert.That(process.ExitCode).IsEqualTo(0);
    }

    private static string LauncherSource([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "src", "Orisumi.Cli", "orisumi-browser-host"));

    private static byte[] Observation(long sequence, string run = Run) => Encoding.UTF8.GetBytes($$"""
        { "format":"orisumi-browser-v1", "instance":"{{Instance}}", "run":"{{run}}", "sequence":{{sequence}}, "observedAt":"2026-11-06T13:00:00.000Z", "kind":"callback", "operation":"tabs.onUpdated", "callbackArgs":[21,{"title":"中文 空格", "url":"https://example.test/?q=one#two"}], "unknown":null }
        """ + "  ");

    private static byte[] Frame(byte[] payload) => [.. BitConverter.GetBytes((uint)payload.Length), .. payload];

    private static byte[] Read(RawStore store, Capture capture, string path)
    {
        using var input = store.Open(capture, path);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static List<JsonElement> Receipts(MemoryStream output)
    {
        output.Position = 0;
        var receipts = new List<JsonElement>();
        var header = new byte[4];
        while (output.Position < output.Length)
        {
            output.ReadExactly(header);
            var payload = new byte[BitConverter.ToUInt32(header)];
            output.ReadExactly(payload);
            using var document = JsonDocument.Parse(payload);
            receipts.Add(document.RootElement.Clone());
        }
        return receipts;
    }

    private sealed class FragmentedInput(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(2, buffer.Length)], cancellationToken);
    }

    private sealed class InterruptedInput(byte[] bytes, int after, Action interrupt) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= after)
            {
                interrupt();
                cancellationToken.ThrowIfCancellationRequested();
                throw new IOException("输入连接关闭");
            }
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, after - (int)Position)], cancellationToken);
        }
    }

    private sealed class CheckingOutput(Action beforeWrite) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            beforeWrite();
            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}
