using Microsoft.Data.Sqlite;

namespace Orisumi.Collectors.Storage;

// Linked into each collector: resources belong to that collector's assembly.
// An existing database is used as is; the structure is not versioned, and capture.mdx says how a changed schema is handled.
internal static class SqliteSchema
{
    private static readonly System.Reflection.Assembly Assembly = typeof(SqliteSchema).Assembly;
    private static readonly string Prefix = Assembly.GetName().Name + ".Schema.";

    internal static void EnsureCreated(SqliteConnection connection, string databaseId)
    {
        // Serialize the empty-database check with creation, including concurrent first opens.
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name NOT GLOB 'sqlite_*'";
        if ((long)command.ExecuteScalar()! == 0)
        {
            var resources = Assembly.GetManifestResourceNames();
            foreach (var group in new[] { "Tables", "Indexes", "Views" })
            {
                foreach (var resource in resources.Where(name => name.StartsWith(Prefix + group + ".", StringComparison.Ordinal)
                             && name.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
                {
                    command.CommandText = Read(resource);
                    try { command.ExecuteNonQuery(); }
                    catch (SqliteException error)
                    {
                        throw new IOException($"Could not initialize {connection.DataSource}: {resource}: {error.Message}", error);
                    }
                }
            }
            command.CommandText = DatabaseIdentity;
            command.Parameters.AddWithValue("$id", databaseId);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    // The new database's identity is created with its structure and equals its file name; DatabaseSchema.swift runs the same statement.
    internal const string DatabaseIdentity =
        "INSERT INTO database_identity VALUES (1, $id, CAST(unixepoch('subsec') * 1000 AS INTEGER))";

    private static string Read(string resource)
    {
        using var stream = Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing schema resource: {resource}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
