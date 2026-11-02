using Orisumi.Application.Data;
using Npgsql;

namespace Orisumi.Infrastructure.Database;

public sealed class PostgresDataStore(NpgsqlDataSource dataSource) : IDataStore
{
    public Task RebuildProjectionsAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(async (transaction, token) =>
        {
            await ((PostgresDataTransaction)transaction).RebuildProjectionsAsync(token);
            return true;
        }, cancellationToken);

    public async Task<T> ExecuteAsync<T>(Func<IDataTransaction, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // The lock is shared by reads and writes, including other API processes.
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(724121180221)", connection, transaction))
            await command.ExecuteNonQueryAsync(cancellationToken);
        var result = await operation(new PostgresDataTransaction(connection, transaction), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken);
        return result;
        // Disposing an uncommitted transaction rolls back on failure/cancellation.
    }
}
