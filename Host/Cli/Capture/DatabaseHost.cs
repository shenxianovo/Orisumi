using Microsoft.Data.Sqlite;
using Orisumi.Platform;

namespace Orisumi.Cli.Capture;

// The host a database was created on, as its database_identity records it (overview/devices.mdx).
internal sealed record DatabaseHost(string? Platform, string? HardwareUuid)
{
    internal static DatabaseHost Read(SqliteConnection database, SqliteTransaction? transaction = null)
    {
        using var command = database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='database_identity'";
        if (command.ExecuteScalar() is 0L) return new(null, null);
        command.CommandText = "SELECT platform, hardware_uuid FROM database_identity";
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)) : new(null, null);
    }

    // Copies from other devices are not this host's capture. A database without an identity is still being
    // created, which only its writer does; unknown local hardware cannot prove a move (Store.WritableDatabase).
    internal bool IsThisHost(HostIdentity host) =>
        Platform is null || Platform == host.Platform && (host.HardwareUuid is null || HardwareUuid == host.HardwareUuid);
}
