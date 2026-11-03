using System.Text.Json;
using System.Xml.Linq;

namespace Orisumi.Dev;

internal enum SourceRole
{
    Production,
    Test,
    Tooling,

    Build,
    Documentation,
    Generated,

    Unclassified,
}

internal enum ProjectKind { CSharp, Node }

internal sealed record Project(
    string Name,
    string Manifest,
    ProjectKind Kind,
    SourceRole Role,
    IReadOnlyList<string> References,
    IReadOnlyList<string> Scripts,
    string? PackageManager = null)
{
    public string Directory { get; } = System.IO.Path.GetDirectoryName(Manifest)!.Replace('\\', '/') + "/";

    public bool Contains(string path) => path.StartsWith(Directory, StringComparison.Ordinal);

    public bool IsImplementation => Role is SourceRole.Production or SourceRole.Tooling;
}

internal sealed class ProjectLayout(IReadOnlyList<Project> projects, string? solution = null)
{
    public const string Scratch = ".scratch/";

    private static readonly Dictionary<string, SourceRole> FolderRoles = new(StringComparer.Ordinal)
    {
        ["src"] = SourceRole.Production,
        ["tests"] = SourceRole.Test,
        ["tools"] = SourceRole.Tooling,
    };

    public IReadOnlyList<Project> Projects => projects;

    public string? Solution => solution;

    public static ProjectLayout Load(string root)
    {
        var solution = Directory.EnumerateFiles(root, "*.slnx").SingleOrDefault();
        if (solution is null) return new([]);
        var entries = XDocument.Load(solution).Descendants()
            .Where(element => element.Name.LocalName is "Project" or "File")
            .Select(element => (Element: element, Path: element.Attribute("Path")!.Value.Replace('\\', '/')))
            .Where(entry => entry.Element.Name.LocalName == "Project" || System.IO.Path.GetFileName(entry.Path) == "package.json");
        return new([.. entries.Select(entry => Read(root, solution, entry.Element, entry.Path))], solution);
    }

    private static Project Read(string root, string solution, XElement element, string path)
    {
        var folder = element.Ancestors().FirstOrDefault(ancestor => ancestor.Name.LocalName == "Folder")?.Attribute("Name")?.Value ?? "/";
        var top = folder.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (!FolderRoles.TryGetValue(top, out var role))
            throw new InvalidOperationException(
                $"{System.IO.Path.GetFileName(solution)}: {path} sits in folder '{folder}'; projects belong under /{string.Join("/, /", FolderRoles.Keys)}/.");
        var full = System.IO.Path.Combine(root, path);
        var directory = System.IO.Path.GetDirectoryName(full)!;
        return element.Name.LocalName == "Project"
            ? new(System.IO.Path.GetFileNameWithoutExtension(path), path, ProjectKind.CSharp, role, References(root, full), [])
            : new(System.IO.Path.GetFileName(directory), path, ProjectKind.Node, role, [], Scripts(full),
                File.Exists(System.IO.Path.Combine(directory, "pnpm-lock.yaml")) ? "pnpm" : "npm");
    }

    private static string[] References(string root, string project) =>
        !File.Exists(project) ? [] : [.. XDocument.Load(project).Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")?.Value)
            .OfType<string>()
            .Select(include => System.IO.Path.GetRelativePath(root,
                System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(project)!, include.Replace('\\', '/'))))!)
                .Replace('\\', '/') + "/")];

    private static string[] Scripts(string packageJson)
    {
        if (!File.Exists(packageJson)) return [];
        using var document = JsonDocument.Parse(File.ReadAllText(packageJson));
        return document.RootElement.TryGetProperty("scripts", out var scripts)
            ? [.. scripts.EnumerateObject().Select(script => script.Name)]
            : [];
    }

    public Project? Owner(string path) =>
        projects.Where(project => project.Contains(path)).MaxBy(project => project.Directory.Length);

    public static bool IsScratch(string path) => path.StartsWith(Scratch, StringComparison.Ordinal);

    public bool TryClassify(string path, out string language, out SourceRole role)
    {
        language = SourceLanguages.Of(path) ?? "";
        role = language.Length == 0 ? SourceRole.Unclassified : Classify(path, language);
        return language.Length > 0;
    }

    public bool IsImplementation(string path) =>
        TryClassify(path, out _, out var role) && role is SourceRole.Production or SourceRole.Tooling;

    private SourceRole Classify(string path, string language) =>
        SourceLanguages.IsGenerated(path) ? SourceRole.Generated
        : language == "Markdown" ? SourceRole.Documentation
        : SourceLanguages.IsBuild(path, language) ? SourceRole.Build
        : Owner(path) is not { } owner ? SourceRole.Unclassified
        : owner.Role == SourceRole.Test || SourceLanguages.IsTest(path[owner.Directory.Length..]) ? SourceRole.Test
        : owner.Role;

    public IEnumerable<Project> TestsCovering(Project project) =>
        projects.Where(candidate => candidate.Kind == ProjectKind.CSharp && candidate.Role == SourceRole.Test
            && Reaches(candidate, project, []));

    private bool Reaches(Project from, Project target, HashSet<string> visited)
    {
        if (from == target) return true;
        if (!visited.Add(from.Directory)) return false;
        return from.References
            .Select(directory => projects.FirstOrDefault(project => project.Directory == directory))
            .OfType<Project>()
            .Any(reference => Reaches(reference, target, visited));
    }
}
