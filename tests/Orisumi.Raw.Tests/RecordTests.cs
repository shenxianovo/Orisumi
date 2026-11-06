using System.Text;

namespace Orisumi.Raw.Tests;

public class RecordTests
{
    private static readonly BlockName Block = BlockName.Of([1]);

    [Test]
    public async Task CaptureRootIsCompactJsonInAFixedOrder()
    {
        var record = new CaptureRecord("codex", "orisumi capture codex 1.0.0", "orisumi-cdc-1", "mac", At(), "/Users/me/.codex",
            At(), null, null, new ManifestReference(Block, [Block]), null);
        await Assert.That(Encoding.UTF8.GetString(RecordJson.Encode(record))).IsEqualTo(
            "{\"kind\":\"capture\",\"source\":\"codex\",\"collector\":\"orisumi capture codex 1.0.0\",\"chunker\":\"orisumi-cdc-1\",\"host\":\"mac\","
            + $"\"capturedAt\":\"2026-11-04T04:44:30.737Z\",\"origin\":\"/Users/me/.codex\",\"from\":\"2026-11-04T04:44:30.737Z\",\"to\":null,"
            + $"\"sourceVersion\":null,\"manifest\":{{\"hash\":\"{Block}\",\"blocks\":[\"{Block}\"]}},\"failure\":null,\"completedAt\":null}}");
    }

    [Test]
    public async Task DeletionRecordNamesItsTarget()
    {
        var deletion = new DeletionRecord(Block, At(), "误采", "mac");
        await Assert.That(Encoding.UTF8.GetString(RecordJson.Encode(deletion))).IsEqualTo(
            $"{{\"kind\":\"deletion\",\"target\":\"{Block}\",\"deletedAt\":\"2026-11-04T04:44:30.737Z\",\"reason\":\"误采\",\"host\":\"mac\"}}");
    }

    [Test]
    public async Task RecordsReadBackAsWritten()
    {
        var record = new CaptureRecord("files", "c", "orisumi-cdc-1", "mac", At(), "/tmp", null, null, "v1", null, new CaptureFailure("目录不存在"));
        var json = RecordJson.Encode(record);
        await Assert.That(RecordJson.KindOf(json)).IsEqualTo("capture");
        await Assert.That(RecordJson.Decode<CaptureRecord>(json)).IsEqualTo(record);
    }

    private static DateTimeOffset At() => new(2026, 11, 4, 4, 44, 30, 737, TimeSpan.Zero);
}
