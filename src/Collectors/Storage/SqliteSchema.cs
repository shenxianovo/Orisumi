using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Orisumi.Collectors.Storage;

// Linked into each collector: resources and version belong to that collector's assembly.
internal static class SqliteSchema
{
    private static readonly System.Reflection.Assembly Assembly = typeof(SqliteSchema).Assembly;
    private static readonly string Prefix = Assembly.GetName().Name + ".Schema.";
    private static readonly int Version = int.Parse(Read(Prefix + "Version.txt"), CultureInfo.InvariantCulture);

    internal static void CheckVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        if ((long)command.ExecuteScalar()! != Version)
            throw Unsupported(connection);
    }

    internal static void EnsureCreated(SqliteConnection connection)
    {
        // Serialize the empty-database check with creation, including concurrent first opens.
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version";
        var actual = (long)command.ExecuteScalar()!;
        if (actual != Version)
        {
            command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name NOT GLOB 'sqlite_*'";
            if (actual != 0 || (long)command.ExecuteScalar()! != 0)
                throw Unsupported(connection);

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
            command.CommandText = $"PRAGMA user_version = {Version}";
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static string Read(string resource)
    {
        using var stream = Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing schema resource: {resource}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static IOException Unsupported(SqliteConnection connection) =>
        new($"Unsupported database schema in {connection.DataSource}. Expected version {Version}. Stop this collector and rebuild its output database.");
}
