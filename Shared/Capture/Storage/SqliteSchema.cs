using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Orisumi.Platform;

namespace Orisumi.Capture.Storage;

// Creates or extends a database from the shared Schema/ and the collector's own Schema/.
public static partial class SqliteSchema
{
    private const string Prefix = "Schema.";

    internal static void Write(Assembly source, TextWriter output)
    {
        using var connection = CreateExpected(source);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.name, c.name, c.type, c."notnull", c.dflt_value, c.pk
            FROM sqlite_schema AS t, pragma_table_info(t.name) AS c
            WHERE t.type = 'table' AND t.name NOT GLOB 'sqlite_*'
            ORDER BY t.name, c.cid
            """;
        using var reader = command.ExecuteReader();
        var tables = new Dictionary<string, List<SchemaColumn>>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var table = reader.GetString(0);
            if (!tables.TryGetValue(table, out var columns)) tables.Add(table, columns = []);
            columns.Add(ReadColumn(reader, 1));
        }
        output.WriteLine(JsonSerializer.Serialize(tables.Select(table => new SchemaTable(table.Key, table.Value.ToArray())).ToArray(),
            ReleaseJson.Default.SchemaTableArray));
    }

    public static void EnsureCreated(SqliteConnection connection, Assembly source, string databaseId, string platform, string? hardwareUuid,
        string? store = null, string? machineGuid = null)
    {
        // Serialize the schema check and any changes with other collector opens.
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name NOT GLOB 'sqlite_*'";
        if ((long)command.ExecuteScalar()! == 0)
        {
            CreateObjects(command, source);
            command.CommandText = DatabaseIdentity;
            command.Parameters.AddWithValue("$id", databaseId);
            command.Parameters.AddWithValue("$platform", platform);
            command.Parameters.AddWithValue("$hardware_uuid", (object?)hardwareUuid ?? DBNull.Value);
            command.Parameters.AddWithValue("$machine_guid", (object?)machineGuid ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        else
        {
            using var expected = CreateExpected(source);
            using var definition = expected.CreateCommand();
            Upgrade(command, definition, store);
        }
        transaction.Commit();
    }

    private static SqliteConnection CreateExpected(Assembly source)
    {
        var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True;Pooling=False");
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            CreateObjects(command, source);
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private static void CreateObjects(SqliteCommand command, Assembly source)
    {
        // Both sets are ordered together by file name within each group, as if they were one directory.
        var resources = new[] { typeof(SqliteSchema).Assembly, source }
            .SelectMany(assembly => assembly.GetManifestResourceNames().Select(name => (Assembly: assembly, Name: name))).ToArray();
        foreach (var group in new[] { "Tables", "Indexes", "Views" })
            foreach (var (assembly, resource) in resources.Where(item => item.Name.StartsWith(Prefix + group + ".", StringComparison.Ordinal)
                         && item.Name.EndsWith(".sql", StringComparison.Ordinal)).OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                try { command.Execute(Read(assembly, resource)); }
                catch (SqliteException error)
                {
                    throw new IOException($"Could not initialize {command.Connection!.DataSource}: {resource}: {error.Message}", error);
                }
            }
    }

    private static void Upgrade(SqliteCommand actual, SqliteCommand expected, string? store)
    {
        var tables = Objects(actual, "table");
        var createTables = new List<string>();
        var addColumns = new List<(string Name, string Sql)>();
        var differences = new List<string>();
        foreach (var (name, sql) in Objects(expected, "table"))
        {
            if (!tables.TryGetValue(name, out var existing)) createTables.Add(sql);
            else if (existing != sql) CompareTable(actual, expected, name, existing, sql, addColumns, differences);
        }
        if (differences.Count != 0) throw Incompatible(actual, store, differences);

        var indexes = Objects(actual, "index");
        var expectedIndexes = Objects(expected, "index");
        var dropIndexes = indexes.Where(index => !SameDefinition(expectedIndexes, index)).Select(index => index.Key).ToArray();
        var createIndexes = expectedIndexes.Where(index => !SameDefinition(indexes, index)).ToArray();
        var views = Objects(actual, "view");
        var expectedViews = Objects(expected, "view");
        if (createTables.Count == 0 && addColumns.Count == 0 && dropIndexes.Length == 0 && createIndexes.Length == 0 &&
            views.Count == expectedViews.Count && views.All(view => SameDefinition(expectedViews, view))) return;

        // A stale view can refer to a missing column and prevent ALTER TABLE from running.
        foreach (var view in views.Keys) actual.Execute($"DROP VIEW {Quote(view)}");
        foreach (var index in dropIndexes) actual.Execute($"DROP INDEX {Quote(index)}");
        foreach (var sql in createTables) actual.Execute(sql);
        foreach (var (column, sql) in addColumns)
        {
            try { actual.Execute(sql); }
            catch (SqliteException error) { throw Incompatible(actual, store, [$"{column}: {error.Message}"], error); }
        }
        foreach (var index in createIndexes) actual.Execute(index.Value);
        foreach (var view in expectedViews.Values) actual.Execute(view);
    }

    private static void CompareTable(SqliteCommand actual, SqliteCommand expected, string table, string actualSql, string expectedSql,
        List<(string Name, string Sql)> addColumns, List<string> differences)
    {
        if (!actualSql.StartsWith("CREATE TABLE ", StringComparison.Ordinal))
        {
            differences.Add($"{table}: table definition changed");
            return;
        }
        var actualColumns = Columns(actual, table);
        var expectedColumns = Columns(expected, table);
        var (actualParts, actualOptions) = TableParts(actualSql);
        var (expectedParts, expectedOptions) = TableParts(expectedSql);
        if (actualParts.Count < actualColumns.Length || expectedParts.Count < expectedColumns.Length)
        {
            differences.Add($"{table}: table definition changed");
            return;
        }
        var existing = actualColumns.Select((column, index) => (Column: column, Part: actualParts[index]))
            .ToDictionary(item => item.Column.Name, StringComparer.Ordinal);
        var expectedNames = expectedColumns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var column in actualColumns)
            if (!expectedNames.Contains(column.Name)) differences.Add($"{table}.{column.Name}: column removed");

        for (var i = 0; i < expectedColumns.Length; i++)
        {
            var column = expectedColumns[i];
            var name = $"{table}.{column.Name}";
            var part = expectedParts[i];
            if (existing.TryGetValue(column.Name, out var previous))
            {
                if (previous.Column.Type != column.Type) differences.Add($"{name}: type changed");
                if (previous.Column.NotNull != column.NotNull) differences.Add($"{name}: notnull changed");
                if (previous.Column.Default != column.Default) differences.Add($"{name}: default changed");
                if (previous.Column.Pk != column.Pk) differences.Add($"{name}: pk changed");
                if (previous.Column == column && previous.Part.Normalized != part.Normalized)
                    differences.Add($"{name}: column definition changed");
            }
            else if (column.NotNull != 0 && column.Default is null) differences.Add($"{name}: new NOT NULL column has no default");
            else if (column.Pk != 0) differences.Add($"{name}: new primary key column");
            else addColumns.Add((name, $"ALTER TABLE {Quote(table)} ADD COLUMN {part.Sql}"));
        }
        if (!actualParts.Skip(actualColumns.Length).Select(part => part.Normalized)
                .SequenceEqual(expectedParts.Skip(expectedColumns.Length).Select(part => part.Normalized)))
            differences.Add($"{table}: table constraints changed");
        if (actualOptions != expectedOptions) differences.Add($"{table}: table options changed");
    }

    private static (List<TablePart> Parts, string Options) TableParts(string sql)
    {
        var parts = new List<TablePart>();
        var tokens = new List<string>();
        var depth = 0;
        var start = -1;
        foreach (Match token in TableTokens().Matches(sql))
        {
            if (token.Groups["trivia"].Success) continue;
            var value = token.Value;
            if (start < 0)
            {
                if (value == "(") { start = token.Index + 1; depth = 1; }
                continue;
            }
            // Commas inside expressions, quoted text or comments are part of the definition.
            if (depth == 1 && value is "," or ")")
            {
                parts.Add(new(sql[start..token.Index].Trim(), string.Join(' ', tokens)));
                tokens.Clear();
                start = token.Index + 1;
                if (value == ")") depth = 0;
                continue;
            }
            if (value == "(") depth++;
            else if (value == ")") depth--;
            tokens.Add(token.Groups["word"].Success ? value.ToUpperInvariant() : value);
        }
        return (parts, string.Join(' ', tokens));
    }

    [GeneratedRegex("""(?<trivia>\s+|--[^\r\n]*|/\*[\s\S]*?\*/)|"(?:""|[^"])*"|'(?:''|[^'])*'|`(?:``|[^`])*`|\[[^\]]*\]|(?<word>[\w$]+)|.""")]
    private static partial Regex TableTokens();

    private sealed record TablePart(string Sql, string Normalized);

    private static SchemaColumn[] Columns(SqliteCommand command, string table)
    {
        command.CommandText = """SELECT name,type,"notnull",dflt_value,pk FROM pragma_table_xinfo($table, 'main') ORDER BY cid""";
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$table", table);
        using var reader = command.ExecuteReader();
        var columns = new List<SchemaColumn>();
        while (reader.Read()) columns.Add(ReadColumn(reader, 0));
        return columns.ToArray();
    }

    private static SchemaColumn ReadColumn(SqliteDataReader reader, int start) => new(reader.GetString(start), reader.GetString(start + 1),
        reader.GetInt64(start + 2), reader.IsDBNull(start + 3) ? null : reader.GetString(start + 3), reader.GetInt64(start + 4));

    private static Dictionary<string, string> Objects(SqliteCommand command, string type)
    {
        command.CommandText = "SELECT name,sql FROM sqlite_schema WHERE type=$type AND name NOT GLOB 'sqlite_*' AND sql IS NOT NULL ORDER BY rowid";
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$type", type);
        using var reader = command.ExecuteReader();
        var objects = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read()) objects.Add(reader.GetString(0), reader.GetString(1));
        return objects;
    }

    private static bool SameDefinition(Dictionary<string, string> objects, KeyValuePair<string, string> entry) =>
        objects.TryGetValue(entry.Key, out var sql) && sql == entry.Value;

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static IOException Incompatible(SqliteCommand command, string? store, IReadOnlyList<string> differences, Exception? inner = null)
    {
        var hint = store is not null && IsDevelopmentStore(store)
            ? "\nRun `orisumi-dev reset` with `--store` pointing to this dev instance." : "";
        return new IOException($"Incompatible database schema in {command.Connection!.DataSource}:\n{string.Join('\n', differences)}{hint}", inner);
    }

    private static bool IsDevelopmentStore(string store)
    {
        try { return Instance.Open(store).Name == "dev"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            return false;
        }
    }

    // The new database records its file ID and host with its structure.
    public const string DatabaseIdentity =
        "INSERT INTO database_identity (singleton,database_id,created_at_utc_ms,platform,hardware_uuid,machine_guid) VALUES (1, $id, CAST(unixepoch('subsec') * 1000 AS INTEGER), $platform, $hardware_uuid, $machine_guid)";

    private static string Read(Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing schema resource: {resource}");
        using var reader = new StreamReader(stream);
        var sql = reader.ReadToEnd();
        return assembly == typeof(SqliteSchema).Assembly ? sql.Replace("'{platform}'", $"'{Platforms.Current}'", StringComparison.Ordinal) : sql;
    }
}
