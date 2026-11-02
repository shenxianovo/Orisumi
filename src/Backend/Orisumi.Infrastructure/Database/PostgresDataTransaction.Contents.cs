using Orisumi.Core;

namespace Orisumi.Infrastructure.Database;

internal sealed partial class PostgresDataTransaction
{
    public async Task<long?> ContentSizeAsync(string digest, CancellationToken cancellationToken) =>
        await ScalarAsync("SELECT size FROM contents WHERE digest = $1", cancellationToken, digest) as long?;

    public Task UploadContentAsync(string digest, byte[] data, DateTimeOffset uploadedAt, CancellationToken cancellationToken) =>
        ExecuteAsync("""
            INSERT INTO contents(digest, size, data, uploaded_at) VALUES($1, $2, $3, $4)
            ON CONFLICT(digest) DO UPDATE SET uploaded_at = excluded.uploaded_at
            """, cancellationToken, digest, (long)data.Length, data, ObservationTime.Normalize(uploadedAt));

    public async Task<byte[]?> ReadContentAsync(string digest, CancellationToken cancellationToken) =>
        await ScalarAsync("""
            SELECT data FROM contents c WHERE digest = $1
            AND EXISTS (SELECT 1 FROM observation_contents v WHERE v.digest = c.digest)
            """, cancellationToken, digest) as byte[];

    public Task PruneContentsAsync(DateTimeOffset before, CancellationToken cancellationToken) => ExecuteAsync("""
        DELETE FROM contents c WHERE uploaded_at < $1
        AND NOT EXISTS (SELECT 1 FROM observation_contents v WHERE v.digest = c.digest)
        """, cancellationToken, ObservationTime.Normalize(before));

    private async Task SaveContentsAsync(Guid observationId, IReadOnlyList<BinaryValue> contents, CancellationToken token)
    {
        if (contents.Count == 0) return;
        await ExecuteAsync("INSERT INTO observation_contents(observation_id, digest) SELECT $1, unnest($2::text[])",
            token, observationId, contents.Select(value => value.Digest).Distinct().ToArray());
    }
}
