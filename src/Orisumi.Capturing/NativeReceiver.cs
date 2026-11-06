using System.Globalization;
using System.Text;
using System.Text.Json;
using Orisumi.Raw;

namespace Orisumi.Capturing;

public sealed class NativeReceiver(string storeDirectory, string collector)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task RunAsync(Stream input, Stream output, string callerOrigin, CancellationToken token = default)
    {
        var store = new RawStore(storeDirectory);
        var connection = Guid.NewGuid();
        long ordinal = 0;
        while (true)
        {
            var frame = new Frame();
            ordinal++;
            var origin = $"caller: {callerOrigin}\nconnection: {connection}\nframe: {ordinal.ToString(CultureInfo.InvariantCulture)}";
            Identity identity;
            try
            {
                if (!await frame.ReadAsync(input, token).ConfigureAwait(false)) return;
                identity = Validate(frame.Payload);
            }
            catch (IOException error)
            {
                Save(store, origin, frame, new CaptureFailure(error.Message));
                throw;
            }
            catch (OperationCanceledException)
            {
                if (frame.HasBytes) Save(store, origin, frame, new CaptureFailure("接收已停止，当前帧尚未取得完整内容"));
                throw;
            }
            origin += $"\ninstance: {identity.Instance}\nrun: {identity.Run}\nsequence: {identity.Sequence.ToString(CultureInfo.InvariantCulture)}";
            frame.Path = "observation.json";
            var capture = Save(store, origin, frame);
            var receipt = JsonSerializer.SerializeToUtf8Bytes(new
            {
                format = "orisumi-browser-ack-v1",
                instance = identity.Instance,
                run = identity.Run,
                sequence = identity.Sequence,
                capture = capture.Name.ToString(),
            });
            try
            {
                await output.WriteAsync(BitConverter.GetBytes((uint)receipt.Length), token).ConfigureAwait(false);
                await output.WriteAsync(receipt, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"浏览器观测已保存为 {capture.Name}，回执写出失败：{error.Message}", error);
            }
        }
    }

    private Capture Save(RawStore store, string origin, Frame frame, CaptureFailure? failure = null)
    {
        var writer = store.BeginCapture(new CaptureStart("browser", collector, origin));
        try
        {
            using var content = frame.Open();
            writer.Add(frame.Path, origin, content, frame.Method);
            return writer.Complete(failure).Capture;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            var reason = $"原件存储失败：{error.Message}";
            if (failure is not null) reason = $"{failure.Reason}；{reason}";
            try
            {
                writer.Fail(reason);
            }
            catch (Exception recordingError) when (recordingError is IOException or UnauthorizedAccessException)
            {
                reason += $"；失败记录也无法保存：{recordingError.Message}";
            }
            throw new IOException(reason, error);
        }
    }

    private static Identity Validate(byte[] payload)
    {
        try
        {
            StrictUtf8.GetCharCount(payload);
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var instance = root.GetProperty("instance").GetString();
            var run = root.GetProperty("run").GetString();
            var sequence = root.GetProperty("sequence").GetInt64();
            if (root.GetProperty("format").GetString() != "orisumi-browser-v1"
                || !Guid.TryParse(instance, out _) || !Guid.TryParse(run, out _)
                || sequence is < 1 or > 9_007_199_254_740_991
                || !root.GetProperty("observedAt").TryGetDateTimeOffset(out _)
                || root.GetProperty("kind").GetString() is not ("started" or "callback" or "query"))
                throw new IOException("浏览器观测的格式、身份、序号、取得时间或种类不符合协议");
            return new Identity(instance!, run!, sequence);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or DecoderFallbackException)
        {
            throw new IOException($"浏览器观测无效：{error.Message}", error);
        }
    }

    private sealed record Identity(string Instance, string Run, long Sequence);

    private sealed class Frame
    {
        private readonly byte[] _header = new byte[4];
        private int _headerBytes;
        private int _payloadBytes;

        public byte[] Payload { get; private set; } = [];
        public bool HasBytes => _headerBytes != 0;
        public string Path { get; set; } = "incomplete-frame.bin";
        public string Method => Path.EndsWith(".json", StringComparison.Ordinal) ? "chrome-native-message-json" : "chrome-native-message-frame";

        public async Task<bool> ReadAsync(Stream input, CancellationToken token)
        {
            while (_headerBytes < _header.Length)
            {
                var count = await input.ReadAsync(_header.AsMemory(_headerBytes), token).ConfigureAwait(false);
                if (count == 0) break;
                _headerBytes += count;
            }
            if (_headerBytes == 0) return false;
            if (_headerBytes != _header.Length) throw new IOException($"Native Messaging 长度头未完成：已取得 {_headerBytes} 字节，预期 4 字节");
            var length = BitConverter.ToUInt32(_header);
            if (length > 64u * 1024 * 1024)
            {
                Path = "invalid-frame.bin";
                throw new IOException($"Native Messaging 消息长度 {length} 超过 64 MiB，已保存长度头");
            }
            Payload = new byte[length];
            while (_payloadBytes < Payload.Length)
            {
                var count = await input.ReadAsync(Payload.AsMemory(_payloadBytes), token).ConfigureAwait(false);
                if (count == 0) break;
                _payloadBytes += count;
            }
            if (_payloadBytes != Payload.Length) throw new IOException($"Native Messaging 内容未完成：已取得 {_payloadBytes} 字节，预期 {length} 字节");
            Path = "invalid-observation.json";
            return true;
        }

        public MemoryStream Open() => new(Path.EndsWith(".json", StringComparison.Ordinal)
            ? Payload : [.. _header.AsSpan(0, _headerBytes), .. Payload.AsSpan(0, _payloadBytes)], writable: false);
    }
}
