using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Orisumi.Dev;

internal sealed partial class ScriptAnalysis(RepositoryContext repository, IProcessRunner runner, ProjectLayout layout)
{
    private readonly NodeTools _tools = new(repository, runner);

    public async Task<CodeScan> ScanAsync(string root, string label, SourceSnapshot snapshot, Run run, CancellationToken cancellationToken)
    {
        await _tools.EnsureAsync(cancellationToken);
        var functions = await ComplexityAsync(root, label, snapshot, run, cancellationToken);
        var unused = new List<DeadCodeFinding>();
        foreach (var project in layout.Projects.Where(project => project.Kind == ProjectKind.Node && project.IsImplementation))
        {
            var directory = Path.Combine(root, project.Directory);
            if (Directory.Exists(directory)) unused.AddRange(await KnipAsync(directory, project, label, run, cancellationToken));
        }
        return new CodeScan(functions, unused);
    }

    private async Task<IReadOnlyList<FunctionMetric>> ComplexityAsync(
        string root, string label, SourceSnapshot snapshot, Run run, CancellationToken cancellationToken)
    {
        var files = snapshot.Files
            .Where(file => file.Role is SourceRole.Production or SourceRole.Tooling && file.Language is "TypeScript" or "JavaScript")
            .Select(file => file.Path).ToArray();
        if (files.Length == 0) return [];
        var list = run.PathOf(label + "-files.json");
        await File.WriteAllTextAsync(list, JsonSerializer.Serialize(files), cancellationToken);
        run.Commands.Add($"eslint {label} implementation files (complexity)");
        var eslint = await runner.CaptureAsync(new(_tools.Bin("eslint"),
            ["--config", _tools.Script("eslint.config.mjs"), "--no-ignore", "--format", "json", .. files], root), cancellationToken);
        await File.WriteAllTextAsync(run.PathOf(label + "-typescript.json"), eslint.StdOut, cancellationToken);
        await File.WriteAllTextAsync(run.PathOf(label + "-typescript.log"), eslint.StdErr, cancellationToken);
        if (eslint.ExitCode is not (0 or 1)) throw new InvalidDataException($"TypeScript complexity scan failed: {eslint.LastLine}");
        run.Commands.Add("node tools/Orisumi.Dev/node-tools/erosion.mjs <source-root>");
        var spans = await runner.CaptureAsync(new("node", [_tools.Script("erosion.mjs"), root, list], root), cancellationToken);
        if (spans.ExitCode != 0) throw new InvalidDataException($"TypeScript erosion scan failed: {spans.LastLine}");
        return ApplyAnalyzerComplexity(ErosionScanner.ParseTypeScriptSpans(spans.StdOut), ParseEslint(root, eslint.StdOut));
    }

    private async Task<IReadOnlyList<DeadCodeFinding>> KnipAsync(
        string directory, Project project, string label, Run run, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Path.Combine(directory, "node_modules")))
        {
            ProcessCommand install = project.PackageManager == "pnpm"
                ? new("pnpm", ["install", "--frozen-lockfile", "--ignore-scripts"], directory)
                : new("npm", ["ci", "--ignore-scripts"], directory);
            run.Commands.Add($"{install} ({label} {project.Name})");
            var restored = await runner.CaptureAsync(install, cancellationToken);
            if (restored.ExitCode != 0) throw new InvalidDataException($"Could not prepare {label} {project.Name} dependencies: {restored.LastLine}");
        }
        var command = new ProcessCommand(_tools.Bin("knip"), ["--directory", directory, "--reporter", "json", "--no-progress"], directory);
        run.Commands.Add(command.ToString());
        var result = await runner.CaptureAsync(command, cancellationToken);
        await File.WriteAllTextAsync(run.PathOf($"{label}-{project.Name}-knip.json"), result.StdOut, cancellationToken);
        await File.WriteAllTextAsync(run.PathOf($"{label}-{project.Name}-knip.log"), result.StdErr, cancellationToken);
        if (result.ExitCode is not (0 or 1)) throw new InvalidDataException($"Knip failed ({label} {project.Name}): {result.LastLine}");
        var findings = ParseKnip(result.StdOut, project.Directory);
        if (result.ExitCode == 1 && findings.Count == 0)
            throw new InvalidDataException($"Knip failed without findings ({label} {project.Name}): {result.LastLine}");
        return findings;
    }

    internal static IReadOnlyList<DeadCodeFinding> ParseKnip(string json, string projectDirectory)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("issues", out var issues) || issues.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Knip did not produce an issues array.");
        var findings = new List<DeadCodeFinding>();
        foreach (var issue in issues.EnumerateArray())
        {
            var path = projectDirectory + issue.GetProperty("file").GetString();
            foreach (var category in issue.EnumerateObject().Where(item => item.Value.ValueKind == JsonValueKind.Array))
            {
                foreach (var item in category.Value.EnumerateArray())
                {
                    var entries = item.ValueKind == JsonValueKind.Array ? item.EnumerateArray().ToArray() : [item];
                    foreach (var entry in entries)
                    {
                        findings.Add(new("knip/" + category.Name, path,
                            entry.TryGetProperty("line", out var line) ? line.GetInt32() : 1,
                            entry.TryGetProperty("name", out var name) ? name.GetString()! : path));
                    }
                }
            }
        }
        return findings;
    }

    internal IReadOnlyList<ComplexityHotspot> ParseEslint(string root, string json)
    {
        using var document = JsonDocument.Parse(json);
        var hotspots = new List<ComplexityHotspot>();
        foreach (var file in document.RootElement.EnumerateArray())
        {
            var path = Path.GetRelativePath(root, file.GetProperty("filePath").GetString()!).Replace('\\', '/');
            if (!layout.IsImplementation(path)) continue;
            foreach (var message in file.GetProperty("messages").EnumerateArray())
            {
                if (message.TryGetProperty("fatal", out var fatal) && fatal.GetBoolean()
                    || message.TryGetProperty("severity", out var severity) && severity.GetInt32() > 1)
                    throw new InvalidDataException($"TypeScript analysis failed at {path}: {message.GetProperty("message").GetString()}");
                if (message.GetProperty("ruleId").GetString() != "complexity") continue;
                var text = message.GetProperty("message").GetString()!;
                var match = ComplexityNumber().Match(text);
                if (!match.Success) continue;
                var separator = text.IndexOf(" has a complexity", StringComparison.Ordinal);
                hotspots.Add(new ComplexityHotspot(
                    "TypeScript", path, message.GetProperty("line").GetInt32(),
                    separator < 0 ? text : text[..separator],
                    int.Parse(match.Groups["complexity"].Value, CultureInfo.InvariantCulture),
                    message.GetProperty("column").GetInt32()));
            }
        }
        return [.. hotspots.OrderBy(item => item.Path, StringComparer.Ordinal).ThenBy(item => item.Line)];
    }

    internal static IReadOnlyList<FunctionMetric> ApplyAnalyzerComplexity(
        IReadOnlyList<FunctionMetric> functions,
        IReadOnlyList<ComplexityHotspot> hotspots)
    {
        var measured = new Dictionary<FunctionMetric, int>();
        foreach (var hotspot in hotspots)
        {
            var kind = hotspot.Symbol.StartsWith("Class field initializer", StringComparison.Ordinal) ? "initializer"
                : hotspot.Symbol.StartsWith("Class static block", StringComparison.Ordinal) ? "static-block" : "function";
            var function = functions.Where(item => item.Path == hotspot.Path && item.Kind == kind && Contains(item, hotspot))
                .OrderBy(item => item.EndLine - item.Line)
                .ThenBy(item => item.EndColumn - item.Column)
                .FirstOrDefault();
            if (function is null || !measured.TryAdd(function, hotspot.Complexity))
                throw new InvalidDataException($"Cannot uniquely match function at {hotspot.Path}:{hotspot.Line}:{hotspot.Column}.");
        }
        if (measured.Count != functions.Count)
            throw new InvalidDataException("The TypeScript analyzer did not measure every function.");
        return [.. functions.Select(function => function with { Complexity = measured[function] })];
    }

    private static bool Contains(FunctionMetric function, ComplexityHotspot diagnostic) =>
        function.EndLine == 0
            ? function.Line == diagnostic.Line && function.Column == diagnostic.Column
            : (diagnostic.Line > function.Line || diagnostic.Line == function.Line && diagnostic.Column >= function.Column)
              && (diagnostic.Line < function.EndLine || diagnostic.Line == function.EndLine && diagnostic.Column < function.EndColumn);

    [GeneratedRegex("complexity of (?<complexity>\\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex ComplexityNumber();
}
