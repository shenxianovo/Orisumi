using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

namespace Orisumi.Cli.Dev.Loc;

internal enum SourceRole { Production, Test, Tooling, Build, Documentation, Generated, Unclassified }

internal sealed record SourceFileMetric(string Path, string Language, SourceRole Role, int Lines, string? Module);

internal sealed record SourceSnapshot(IReadOnlyList<SourceFileMetric> Files)
{
    public IReadOnlyList<string> UnclassifiedPaths => [.. Files
        .Where(file => file.Role == SourceRole.Unclassified)
        .Select(file => file.Path)
        .Order(StringComparer.Ordinal)];

    public static async Task<SourceSnapshot> ReadAsync(string root, CancellationToken token)
    {
        var paths = (await GitFiles(root, token))
            .Where(path => File.Exists(Path.Combine(root, path)))
            .Where(path => !File.GetAttributes(Path.Combine(root, path)).HasFlag(FileAttributes.ReparsePoint))
            .ToArray();
        var projects = paths.Where(path => path.EndsWith(".csproj", StringComparison.Ordinal)
                || Path.GetFileName(path) == "package.json")
            .Select(path => Project.Read(root, path))
            .OrderByDescending(project => project.Directory.Length)
            .ToArray();
        var files = new List<SourceFileMetric>();
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            if (SourceLanguages.Of(path) is not { } language) continue;
            var owner = projects.FirstOrDefault(project => path.StartsWith(project.Directory, StringComparison.Ordinal));
            var role = Role(path, language, owner);
            var module = owner?.Name;
            if (owner is { IsTest: true })
            {
                var referenced = projects.Where(project => !project.IsTest && owner.References.Contains(project.Manifest)).ToArray();
                module = referenced is [var project] ? project.Name : LocReport.CrossModule;
            }
            else if (owner is null)
                module = role == SourceRole.Test ? LocReport.CrossModule : role == SourceRole.Tooling ? "Scripts" : null;
            var text = await File.ReadAllTextAsync(Path.Combine(root, path), token);
            files.Add(new(path, language, role, SourceLineCounter.Count(text, language), module));
        }
        return new(files);
    }

    private static SourceRole Role(string path, string language, Project? owner) =>
        SourceLanguages.IsGenerated(path) ? SourceRole.Generated
        : language == "Markdown" ? SourceRole.Documentation
        : SourceLanguages.IsBuild(path, language) ? SourceRole.Build
        : owner is { IsTest: true } || SourceLanguages.IsTest(path) ? SourceRole.Test
        : path.StartsWith("scripts/", StringComparison.Ordinal)
            || path.StartsWith("tools/", StringComparison.Ordinal)
            || path.StartsWith("src/Orisumi.Cli/Dev/", StringComparison.Ordinal) ? SourceRole.Tooling
        : owner is null ? SourceRole.Unclassified : SourceRole.Production;

    private sealed record Project(string Name, string Manifest, bool IsTest, string[] References)
    {
        public string Directory { get; } = Path.GetDirectoryName(Manifest)!.Replace('\\', '/') is { Length: > 0 } directory
            ? directory + "/" : "";

        public static Project Read(string root, string path)
        {
            var full = Path.Combine(root, path);
            if (Path.GetFileName(path) == "package.json")
            {
                using var document = JsonDocument.Parse(File.ReadAllText(full));
                var name = document.RootElement.TryGetProperty("name", out var value) ? value.GetString() : null;
                return new(name ?? new DirectoryInfo(Path.GetDirectoryName(full)!).Name, path, SourceLanguages.IsTest(path), []);
            }
            XDocument project;
            try { project = XDocument.Load(full); }
            catch (System.Xml.XmlException error) { throw new InvalidDataException($"{path}: {error.Message}", error); }
            var references = project.Descendants().Where(element => element.Name.LocalName == "ProjectReference")
                .Select(element => element.Attribute("Include")?.Value).OfType<string>()
                .Select(include => Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(Path.GetDirectoryName(full)!, include.Replace('\\', '/'))))
                    .Replace('\\', '/')).ToArray();
            var isTest = SourceLanguages.IsTest(path) || project.Descendants().Any(element =>
                element.Name.LocalName == "IsTestProject" && element.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
            return new(Path.GetFileNameWithoutExtension(path), path, isTest, references);
        }
    }

    private static async Task<string[]> GitFiles(string root, CancellationToken token)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "ls-files", "--cached", "--others", "--exclude-standard", "-z" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法读取 Git 文件清单。");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(output, error);
            throw;
        }
        var paths = await output;
        var diagnostic = await error;
        if (process.ExitCode != 0) throw new IOException(diagnostic.Trim());
        return paths.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }
}
