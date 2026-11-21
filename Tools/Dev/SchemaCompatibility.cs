using Orisumi.Platform;

namespace Orisumi.Dev;

internal static class SchemaCompatibility
{
    internal static List<string> Violations(ReleaseManifest previous, ReleaseManifest release)
    {
        var violations = new List<string>();
        foreach (var (source, tables) in previous.Schema.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (!release.Schema.TryGetValue(source, out var current))
            {
                violations.Add($"{source}: source removed");
                continue;
            }
            var currentTables = current.ToDictionary(table => table.Name, StringComparer.Ordinal);
            foreach (var table in tables)
            {
                var name = $"{source}.{table.Name}";
                if (!currentTables.TryGetValue(table.Name, out var next))
                {
                    violations.Add($"{name}: table removed");
                    continue;
                }
                var currentColumns = next.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
                var previousColumns = table.Columns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var column in table.Columns)
                {
                    var path = $"{name}.{column.Name}";
                    if (!currentColumns.TryGetValue(column.Name, out var newColumn))
                    {
                        violations.Add($"{path}: column removed");
                        continue;
                    }
                    if (column.Type != newColumn.Type) violations.Add($"{path}: type changed");
                    if (column.NotNull != newColumn.NotNull) violations.Add($"{path}: notnull changed");
                    if (column.Default != newColumn.Default) violations.Add($"{path}: default changed");
                    if (column.Pk != newColumn.Pk) violations.Add($"{path}: pk changed");
                }
                foreach (var column in next.Columns)
                    if (!previousColumns.Contains(column.Name) && column.NotNull != 0 && column.Default is null)
                        violations.Add($"{name}.{column.Name}: new NOT NULL column has no default");
            }
        }
        return violations;
    }
}
