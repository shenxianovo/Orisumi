using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Orisumi.Capture.Storage;

// Builds a new database from the shared Schema/ in this assembly and the source's own Schema/ in the collector's.
// An existing database is used as is; the structure is not versioned, and capture/index.mdx says how a changed schema is handled.
public static class SqliteSchema
{
    private const string Prefix = "Schema.";

    public static void EnsureCreated(SqliteConnection connection, Assembly source, string databaseId, string platform, string? hardwareUuid)
    {
        // Serialize the empty-database check with creation, including concurrent first opens.
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name NOT GLOB 'sqlite_*'";
        if ((long)command.ExecuteScalar()! == 0)
        {
            // Both sets are ordered together by file name within each group, as if they were one directory.
            var resources = new[] { typeof(SqliteSchema).Assembly, source }
                .SelectMany(assembly => assembly.GetManifestResourceNames().Select(name => (Assembly: assembly, Name: name))).ToArray();
            foreach (var group in new[] { "Tables", "Indexes", "Views" })
            {
                foreach (var (assembly, resource) in resources.Where(item => item.Name.StartsWith(Prefix + group + ".", StringComparison.Ordinal)
                             && item.Name.EndsWith(".sql", StringComparison.Ordinal)).OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    command.CommandText = Read(assembly, resource);
                    try { command.ExecuteNonQuery(); }
                    catch (SqliteException error)
                    {
                        throw new IOException($"Could not initialize {connection.DataSource}: {resource}: {error.Message}", error);
                    }
                }
            }
            command.CommandText = DatabaseIdentity;
            command.Parameters.AddWithValue("$id", databaseId);
            command.Parameters.AddWithValue("$platform", platform);
            command.Parameters.AddWithValue("$hardware_uuid", (object?)hardwareUuid ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    // The new database records its file ID and host with its structure.
    public const string DatabaseIdentity =
        "INSERT INTO database_identity VALUES (1, $id, CAST(unixepoch('subsec') * 1000 AS INTEGER), $platform, $hardware_uuid)";

    private static string Read(Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing schema resource: {resource}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
