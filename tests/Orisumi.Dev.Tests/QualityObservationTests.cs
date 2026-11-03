using Orisumi.Dev;

namespace Orisumi.Dev.Tests;

public sealed class QualityObservationTests
{
    private static readonly ProjectLayout Layout = new(
    [
        new("Example", "src/Example/Example.csproj", ProjectKind.CSharp, SourceRole.Production, [], []),
        new("Example.Tests", "tests/Example.Tests/Example.Tests.csproj", ProjectKind.CSharp, SourceRole.Test, ["src/Example/"], []),
        new("Orisumi.Docs", "src/Docs/Orisumi.Docs/package.json", ProjectKind.Node, SourceRole.Production, [], [], "pnpm"),
    ]);

    [Test]
    public async Task AnalysisBuildsOnlyImplementationProjectsFromTheSolutionOfThatRevision()
    {
        var root = Path.Combine(Path.GetTempPath(), "orisumi-analysis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "PreviousName.slnx"), """
                <Solution>
                  <Folder Name="/src/"><Project Path="src/Example/Example.csproj" /><File Path="src/Docs/Orisumi.Docs/package.json" /></Folder>
                  <Folder Name="/tests/"><Project Path="tests/Example.Tests/Example.Tests.csproj" /></Folder>
                </Solution>
                """);
            var (path, projects) = CSharpAnalysis.Prepare(root, Layout, Path.Combine(root, "analysis.slnx"));
            var solution = System.Xml.Linq.XDocument.Load(path);
            await Assert.That(projects).IsEqualTo(1);
            await Assert.That(solution.Descendants("Project").Single().Attribute("Path")!.Value)
                .IsEqualTo(Path.Combine(root, "src", "Example", "Example.csproj"));
            await Assert.That(solution.Descendants("File").Any()).IsFalse();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public async Task TypeScriptParsingErrorsCannotBecomeAnEmptySuccessfulScan()
    {
        var path = Path.Combine(Path.GetTempPath(), "src", "Docs", "Orisumi.Docs", "lib", "example.ts");
        var json = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { filePath = path, messages = new[] { new { ruleId = (string?)null, fatal = true, severity = 2, message = "Unexpected token ;", line = 1, column = 23 } } },
        });
        var analysis = new ScriptAnalysis(new RepositoryContext(Path.GetTempPath()), new FixedRunner(0), Layout);
        await Assert.That(() => analysis.ParseEslint(Path.GetTempPath(), json)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task OnlyFunctionsThatGrewMoreComplexOrAppearedAreRegressions()
    {
        ComplexityHotspot Hotspot(string symbol, int complexity) => new("C#", "src/Example/A.cs", 1, symbol, complexity);
        var regressions = QualityAnalysis.Regressions(
            [Hotspot("Same", 12), Hotspot("Grew", 11)],
            [Hotspot("Same", 12), Hotspot("Grew", 14), Hotspot("New", 11)]);
        await Assert.That(string.Join(',', regressions.Select(item => $"{item.Current.Symbol}:{item.BaseComplexity}"))).IsEqualTo("Grew:11,New:");
    }
}
