using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Orisumi.Raw;

namespace Orisumi.Capturing;

public sealed class NativeReceiver(string storeDirectory, string collector)
{
    private const string FragmentFormat = "orisumi-browser-fragment-v1";
    private const long MaximumSafeInteger = 9_007_199_254_740_991;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = int.MaxValue };

    public async Task RunAsync(Stream input, Stream output, string callerOrigin, CancellationToken token = default)
    {
        var store = new RawStore(storeDirectory);
        var connection = Guid.NewGuid();
        long ordinal = 0;
        Transfer? transfer = null;
        try
        {
            while (true)
            {
                var frame = new Frame();
                ordinal++;
                var origin = $"caller: {callerOrigin}\nconnection: {connection}\nframe: {ordinal.ToString(CultureInfo.InvariantCulture)}";
                Identity identity;
                try
                {
                    if (!await frame.ReadAsync(input, token).ConfigureAwait(false))
                    {
                        if (transfer is null) return;
                        throw new IOException("分片传输结束，原件尚未收齐");
                    }
                    StrictUtf8.GetCharCount(frame.Payload);
                    using var document = JsonDocument.Parse(frame.Payload, JsonOptions);
                    var root = document.RootElement;
                    if (root.GetProperty("format").GetString() == FragmentFormat)
                    {
                        frame.Path = "invalid-fragment.json";
                        var fragment = Fragment.Read(root);
                        transfer ??= new Transfer(fragment, ordinal);
                        transfer.Append(fragment);
                        if (!transfer.IsComplete) continue;
                        transfer.CheckHash();
                        identity = await ValidateAsync(transfer.Content, token).ConfigureAwait(false);
                        if (identity != transfer.Identity) throw new IOException("重组原件身份与分片声明不符");
                    }
                    else
                    {
                        if (transfer is not null)
                        {
                            frame.Path = "invalid-fragment.json";
                            throw new IOException("分片原件未完成，连接收到另一种消息");
                        }
                        identity = Validate(root);
                    }
                }
                catch (OperationCanceledException)
                {
                    if (frame.HasBytes || transfer is not null)
                        Save(store, origin, frame, transfer, new CaptureFailure("接收已停止，当前原件尚未取得完整内容"));
                    throw;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException
                    or InvalidOperationException or FormatException or DecoderFallbackException)
                {
                    var reason = $"浏览器观测无效：{error.Message}";
                    Save(store, origin, frame, transfer, new CaptureFailure(reason));
                    throw new IOException(reason, error);
                }
                origin += $"\ninstance: {identity.Instance}\nrun: {identity.Run}\nsequence: {identity.Sequence.ToString(CultureInfo.InvariantCulture)}";
                frame.Path = "observation.json";
                var capture = Save(store, origin, transfer is null ? frame : null, transfer);
                transfer?.Dispose();
                transfer = null;
                await AcknowledgeAsync(output, identity, capture, token).ConfigureAwait(false);
            }
        }
        finally
        {
            transfer?.Dispose();
        }
    }

    private static async Task AcknowledgeAsync(Stream output, Identity identity, Capture capture, CancellationToken token)
    {
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

    private Capture Save(RawStore store, string origin, Frame? frame, Transfer? transfer = null, CaptureFailure? failure = null)
    {
        if (transfer is not null) origin += transfer.Describe();
        var writer = store.BeginCapture(new CaptureStart("browser", collector, origin));
        try
        {
            if (transfer is not null)
            {
                transfer.Content.Position = 0;
                var path = failure is null ? "observation.json" : transfer.IsComplete ? "invalid-observation.json" : "observation-prefix.bin";
                writer.Add(path, origin, transfer.Content, transfer.IsComplete ? "chrome-native-fragment-json" : "chrome-native-message-json-prefix");
            }
            if (frame is { HasBytes: true } && transfer?.IsComplete != true)
            {
                using var content = frame.Open();
                writer.Add(frame.Path, origin, content, frame.Method);
            }
            var capture = writer.Complete(failure).Capture;
            if (transfer is not null) transfer.Preserved = true;
            return capture;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            var reason = $"原件存储失败：{error.Message}";
            if (failure is not null) reason = $"{failure.Reason}；{reason}";
            if (transfer is not null) reason += $"；已取得前缀的临时文件保留在 {transfer.Path}";
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

    private static async Task<Identity> ValidateAsync(Stream content, CancellationToken token)
    {
        content.Position = 0;
        using (var reader = new StreamReader(content, StrictUtf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
        {
            var characters = new char[16 * 1024];
            while (await reader.ReadAsync(characters, token).ConfigureAwait(false) != 0) { }
        }
        content.Position = 0;
        using var document = await JsonDocument.ParseAsync(content, JsonOptions, token).ConfigureAwait(false);
        return Validate(document.RootElement);
    }

    private static Identity Validate(JsonElement root)
    {
        var identity = ReadIdentity(root);
        if (root.GetProperty("format").GetString() != "orisumi-browser-v1"
            || !root.GetProperty("observedAt").TryGetDateTimeOffset(out _)
            || root.GetProperty("kind").GetString() is not ("started" or "callback" or "query" or "status"))
            throw new IOException("浏览器观测的格式、身份、序号、取得时间或种类不符合协议");
        return identity;
    }

    private static Identity ReadIdentity(JsonElement root)
    {
        var instance = root.GetProperty("instance").GetString();
        var run = root.GetProperty("run").GetString();
        var sequence = root.GetProperty("sequence").GetInt64();
        if (!Guid.TryParse(instance, out _) || !Guid.TryParse(run, out _) || sequence is < 1 or > MaximumSafeInteger)
            throw new IOException("浏览器观测身份或序号不符合协议");
        return new Identity(instance!, run!, sequence);
    }

    private sealed record Identity(string Instance, string Run, long Sequence);

    private sealed record Fragment(Identity Identity, long ByteLength, string Hash, long Offset, byte[] Data)
    {
        public static Fragment Read(JsonElement root)
        {
            var identity = ReadIdentity(root);
            var length = root.GetProperty("byteLength").GetInt64();
            var offset = root.GetProperty("offset").GetInt64();
            var hash = root.GetProperty("hash").GetString();
            var encoded = root.GetProperty("data").GetString();
            if (length is < 1 or > MaximumSafeInteger || offset < 0 || offset >= length)
                throw new IOException("分片总长或偏移不符合协议");
            if (hash is not { Length: 71 } || !hash.StartsWith("sha256-", StringComparison.Ordinal)
                || hash.AsSpan(7).ContainsAnyExcept("0123456789abcdef".AsSpan()))
                throw new IOException("分片 hash 不是 sha256 加小写十六进制");
            if (encoded is not { Length: > 0 and <= 1_398_104 }) throw new IOException("分片 base64 长度不符合协议");
            var data = Convert.FromBase64String(encoded);
            if (data.Length is < 1 or > 1_048_576 || Convert.ToBase64String(data) != encoded)
                throw new IOException("分片不是规范 base64 或超过 1 MiB");
            if (data.Length > length - offset) throw new IOException("分片内容超过声明原件总长");
            return new Fragment(identity, length, hash, offset, data);
        }
    }

    private sealed class Transfer : IDisposable
    {
        private readonly long _firstFrame;
        private readonly long _byteLength;
        private readonly string _hash;
        private readonly IncrementalHash _digest;

        public Transfer(Fragment first, long ordinal)
        {
            if (first.Offset != 0) throw new IOException("分片首帧必须从 offset 0 开始");
            Identity = first.Identity;
            _firstFrame = ordinal;
            _byteLength = first.ByteLength;
            _hash = first.Hash;
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"orisumi-browser-{Guid.NewGuid():N}.partial");
            Content = new FileStream(Path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            _digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        }

        public Identity Identity { get; }
        public string Path { get; }
        public FileStream Content { get; }
        public bool IsComplete => Content.Length == _byteLength;
        public bool Preserved { get; set; }

        public void Append(Fragment fragment)
        {
            if (fragment.Identity != Identity || fragment.ByteLength != _byteLength || fragment.Hash != _hash)
                throw new IOException("分片身份、总长或 hash 与首帧声明不符");
            if (fragment.Offset != Content.Length) throw new IOException("分片 offset 与已取得的连续字节数不符");
            Content.Write(fragment.Data);
            _digest.AppendData(fragment.Data);
        }

        public void CheckHash()
        {
            var actual = "sha256-" + Convert.ToHexStringLower(_digest.GetHashAndReset());
            if (actual != _hash) throw new IOException($"重组原件 hash 不符：声明 {_hash}，实际 {actual}");
        }

        public string Describe() => FormattableString.Invariant($"\nfirst frame: {_firstFrame}\ndeclared instance: {Identity.Instance}\ndeclared run: {Identity.Run}\ndeclared sequence: {Identity.Sequence}\ndeclared byte length: {_byteLength}\ndeclared hash: {_hash}\nreceived bytes: {Content.Length}");

        public void Dispose()
        {
            Content.Dispose();
            _digest.Dispose();
            if (Preserved) File.Delete(Path);
        }
    }

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
