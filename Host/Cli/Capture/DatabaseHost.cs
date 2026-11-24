using Microsoft.Data.Sqlite;
using Orisumi.Platform;

namespace Orisumi.Cli.Capture;

// The host a database was created on, as its database_identity records it (overview/devices.mdx).
internal sealed record DatabaseHost(string? Platform, string? HardwareUuid, string? MachineGuid = null)
{
    internal static DatabaseHost Read(SqliteConnection database, SqliteTransaction? transaction = null)
    {
        using var command = database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='database_identity'";
        if (command.ExecuteScalar() is 0L) return new(null, null);
        command.CommandText = "SELECT * FROM database_identity LIMIT 1";
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(Column("platform"), Column("hardware_uuid"), Column("machine_guid")) : new(null, null);

        string? Column(string name)
        {
            for (var i = 0; i < reader.FieldCount; i++)
                if (reader.GetName(i) == name) return reader.IsDBNull(i) ? null : reader.GetString(i);
            return null;
        }
    }

    internal string DeviceKey(string databaseId)
    {
        if (Platform is not null && HostIdentity.DeviceUuid(Platform, HardwareUuid) is { } uuid)
            return $"{Platform}:{(Platform == "windows" ? uuid.ToUpperInvariant() : uuid)}";
        if (Platform == "windows" && MachineGuid is not null)
            return $"windows:machine-guid:{MachineGuid.ToUpperInvariant()}";
        return $"db:{databaseId}";
    }

    // Copies from other devices are not this host's capture. A database without an identity is still being
    // created, which only its writer does; unknown local hardware cannot prove a move (Store.WritableDatabase).
    internal bool IsThisHost(HostIdentity host) =>
        Platform is null || host.MatchesDatabase(Platform, HardwareUuid, MachineGuid);
}
