namespace Orisumi.Dev;

internal enum VerificationScope { Changed, All }

internal sealed record VerificationRequest(VerificationScope Scope, string? Base, bool PlanOnly, bool Json);

internal sealed record VerificationPlan(
    VerificationScope Scope, bool Fallback, string? Base, IReadOnlyList<string> ChangedPaths, IReadOnlyList<ICheck> Checks)
{
    public string ScopeName => Scope == VerificationScope.All ? "all" : "changed";

    public string Title => $"verify {ScopeName}";
}

internal sealed record VerificationRecord(
    string Evidence, string Scope, bool Fallback, string? Base, IReadOnlyList<string> ChangedPaths,
    int ExitCode, IReadOnlyList<CheckRecord> Checks);

internal sealed class VerificationPlanner(RepositoryContext repository, IProcessRunner runner)
{
    public static readonly string[] Scripts = ["test", "types:check", "build"];

    public async Task<VerificationPlan> CreateAsync(VerificationRequest request, CancellationToken cancellationToken)
    {
        if (request.Scope == VerificationScope.All && request.Base is not null)
            throw new CommandUsageException("--base requires --scope changed.");
        var layout = ProjectLayout.Load(repository.Root);
        if (request.Scope == VerificationScope.All)
            return new(VerificationScope.All, false, null, [], [new FormatCheck(repository, runner, null), .. Checks(layout, Everything(layout))]);
        var baseRef = await repository.Git.BaseAsync(request.Base, cancellationToken);
        var paths = await repository.Git.ChangedPathsAsync(baseRef, cancellationToken);
        var (checks, fallback) = ChecksFor(layout, paths);
        ICheck[] tidy = paths.Any(Formatter.Covers) ? [new FormatCheck(repository, runner, paths)] : [];
        return new(VerificationScope.Changed, fallback, baseRef, paths, [.. tidy, .. checks]);
    }

    internal (IReadOnlyList<ICheck> Checks, bool Fallback) ChecksFor(ProjectLayout layout, IReadOnlyList<string> paths) =>
        Select(layout, paths) is { } selection ? (Checks(layout, selection), false) : (Checks(layout, Everything(layout)), true);

    private sealed class Selection
    {
        public bool Lint { get; set; }
        public bool Build { get; set; }
        public HashSet<Project> Tests { get; } = [];
        public HashSet<(Project Project, string Script)> Scripts { get; } = [];
    }

    private static Selection? Select(ProjectLayout layout, IEnumerable<string> paths)
    {
        var selection = new Selection();
        foreach (var path in paths.Where(path => !ProjectLayout.IsScratch(path)))
        {
            var markdown = DocumentLint.Covers(path);
            if (markdown || CommentLint.Covers(path)) selection.Lint = true;
            switch (layout.Owner(path))
            {
                case null or { Kind: ProjectKind.CSharp } when markdown:
                    break;
                case null:
                    return null;
                case { Kind: ProjectKind.CSharp } project:
                    selection.Build = true;
                    selection.Tests.UnionWith(layout.TestsCovering(project));
                    break;
                case var project:
                    foreach (var script in Scripts.Where(project.Scripts.Contains)) selection.Scripts.Add((project, script));
                    break;
            }
        }
        return selection;
    }

    private static Selection Everything(ProjectLayout layout)
    {
        var selection = new Selection { Lint = true, Build = layout.Projects.Any(project => project.Kind == ProjectKind.CSharp) };
        selection.Tests.UnionWith(layout.Projects.Where(project => project.Kind == ProjectKind.CSharp && project.Role == SourceRole.Test));
        foreach (var project in layout.Projects)
        {
            foreach (var script in Scripts.Where(project.Scripts.Contains)) selection.Scripts.Add((project, script));
        }
        return selection;
    }

    private ICheck[] Checks(ProjectLayout layout, Selection selection) =>
    [
        .. selection.Lint ? [new LintCheck(repository, runner)] : Array.Empty<ICheck>(),
        .. selection.Build && layout.Solution is { } solution
            ? [new ProcessCheck("dotnet build", new("dotnet", ["build", solution, "--verbosity", "minimal"]), runner, repository)]
            : Array.Empty<ICheck>(),
        .. layout.Projects.Where(selection.Tests.Contains).Select(project => new TestCheck($"dotnet test {project.Name}",
            new("dotnet", ["test", "--project", repository.Path(project.Manifest)]), ProjectKind.CSharp, runner, repository)),
        .. layout.Projects.SelectMany(project => Scripts
            .Where(script => selection.Scripts.Contains((project, script)))
            .Select(script => Script(project, script))),
    ];

    private ICheck Script(Project project, string script)
    {
        ProcessCommand command = new(project.PackageManager!, ["run", script], repository.Path(project.Directory));
        return script == "test"
            ? new TestCheck($"{project.Name} {script}", command, ProjectKind.Node, runner, repository)
            : new ProcessCheck($"{project.Name} {script}", command, runner, repository);
    }
}
