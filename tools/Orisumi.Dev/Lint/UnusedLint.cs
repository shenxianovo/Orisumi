using System.Text.Json;

namespace Orisumi.Dev;

internal sealed record DeclaredException(string Rule, string Path, string Symbol, string Reason);

internal sealed record UnusedFinding(string Rule, string Path, int Line, string Symbol)
{
    public bool Matches(DeclaredException exception) => Rule == exception.Rule && Path == exception.Path && Symbol == exception.Symbol;
}

internal sealed class UnusedLint(RepositoryContext repository, IProcessRunner runner, ProjectLayout layout)
{
    public const string ExceptionsFile = "exceptions.json";
    public const string ReasonQuestion = "unused-reason";

    private sealed record ExceptionFile(string? Rule, string? Path, string? Symbol, string? Reason);

    public IEnumerable<Project> Projects => layout.Projects.Where(project => project.Kind == ProjectKind.Node && project.IsImplementation);

    public async Task<IReadOnlyList<LintFinding>> ScanAsync(IReadOnlyList<string>? changed, CancellationToken cancellationToken)
    {
        var reasons = Reasons();
        var findings = new List<LintFinding>();
        foreach (var project in Projects.Where(project => changed is null || changed.Any(project.Contains)))
        {
            var (exceptions, problems) = Exceptions(project, reasons);
            findings.AddRange(problems);
            var unused = await KnipAsync(project, cancellationToken);
            findings.AddRange(unused.Where(finding => !exceptions.Any(finding.Matches))
                .Select(finding => new LintFinding(finding.Rule, finding.Path, finding.Line, $"{finding.Symbol} is unused")));
            findings.AddRange(exceptions.Where(exception => !unused.Any(finding => finding.Matches(exception)))
                .Select(exception => new LintFinding("exception", project.Directory + ExceptionsFile, 1,
                    $"{exception.Symbol} ({exception.Rule}) is no longer reported; remove the exception")));
        }
        return findings;
    }

    public (IReadOnlyList<DeclaredException> Exceptions, IReadOnlyList<LintFinding> Problems) Exceptions(Project project, IReadOnlyList<string> reasons)
    {
        var file = project.Directory + ExceptionsFile;
        var path = repository.Path(file);
        if (!File.Exists(path)) return ([], []);
        var exceptions = new List<DeclaredException>();
        var problems = new List<LintFinding>();
        foreach (var entry in JsonSerializer.Deserialize<ExceptionFile[]>(File.ReadAllText(path), JsonOptions.Indented) ?? [])
        {
            if (entry is not { Rule.Length: > 0, Path.Length: > 0, Symbol.Length: > 0, Reason.Length: > 0 })
                problems.Add(new LintFinding("exception", file, 1, "every exception needs rule, path, symbol and reason"));
            else if (!reasons.Contains(entry.Reason))
                problems.Add(new LintFinding("exception", file, 1, $"unknown reason '{entry.Reason}'; reasons are {string.Join(", ", reasons)}"));
            else exceptions.Add(new DeclaredException(entry.Rule, project.Directory + entry.Path, entry.Symbol, entry.Reason));
        }
        return (exceptions, problems);
    }

    public IReadOnlyList<string> Reasons() =>
        Questions.Load(repository, layout).FirstOrDefault(question => question.Id == ReasonQuestion) is { } question
            ? [.. question.Options.Select(option => option.Value).Where(value => value != "none")]
            : [];

    private async Task<IReadOnlyList<UnusedFinding>> KnipAsync(Project project, CancellationToken cancellationToken)
    {
        var directory = repository.Path(project.Directory);
        if (!Directory.Exists(Path.Combine(directory, "node_modules")))
        {
            ProcessCommand install = project.PackageManager == "pnpm"
                ? new("pnpm", ["install", "--frozen-lockfile", "--ignore-scripts"], directory)
                : new("npm", ["ci", "--ignore-scripts"], directory);
            var restored = await runner.CaptureAsync(install, cancellationToken);
            if (restored.ExitCode != 0) throw new InvalidOperationException($"Could not prepare {project.Name} dependencies: {restored.LastLine}");
        }
        var tools = new NodeTools(repository, runner);
        await tools.EnsureAsync(cancellationToken);
        var result = await runner.CaptureAsync(new(tools.Bin("knip"), ["--directory", directory, "--reporter", "json", "--no-progress"], directory), cancellationToken);
        if (result.ExitCode is not (0 or 1)) throw new InvalidOperationException($"Knip failed in {project.Name}: {result.LastLine}");
        var findings = ParseKnip(result.StdOut, project.Directory);
        if (result.ExitCode == 1 && findings.Count == 0) throw new InvalidOperationException($"Knip failed without findings in {project.Name}: {result.LastLine}");
        return findings;
    }

    internal static IReadOnlyList<UnusedFinding> ParseKnip(string json, string projectDirectory)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("issues", out var issues) || issues.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Knip did not produce an issues array.");
        var findings = new List<UnusedFinding>();
        foreach (var issue in issues.EnumerateArray())
        {
            var path = projectDirectory + issue.GetProperty("file").GetString();
            foreach (var category in issue.EnumerateObject().Where(item => item.Value.ValueKind == JsonValueKind.Array))
            {
                foreach (var item in category.Value.EnumerateArray())
                {
                    foreach (var entry in item.ValueKind == JsonValueKind.Array ? item.EnumerateArray().ToArray() : [item])
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
}
