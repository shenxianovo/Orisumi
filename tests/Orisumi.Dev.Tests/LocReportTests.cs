using Orisumi.Dev;

namespace Orisumi.Dev.Tests;

public sealed class LocReportTests
{
    private static readonly ProjectLayout Layout = new(
    [
        new("Orisumi.Core", "src/Core/Orisumi.Core/Orisumi.Core.csproj", ProjectKind.CSharp, SourceRole.Production, [], []),
        new("Orisumi.Api", "src/Backend/Orisumi.Api/Orisumi.Api.csproj", ProjectKind.CSharp, SourceRole.Production, [], []),
        new("Orisumi.Web", "src/Clients/Orisumi.Web/package.json", ProjectKind.Node, SourceRole.Production, [], [], "pnpm"),
        new("Orisumi.Testing", "tests/Orisumi.Testing/Orisumi.Testing.csproj", ProjectKind.CSharp, SourceRole.Test, [], []),
        new("Orisumi.Model.Tests", "tests/Orisumi.Model.Tests/Orisumi.Model.Tests.csproj", ProjectKind.CSharp, SourceRole.Test,
            ["src/Core/Orisumi.Core/", "tests/Orisumi.Testing/"], []),
        new("Orisumi.Integration.Tests", "tests/Orisumi.Integration.Tests/Orisumi.Integration.Tests.csproj", ProjectKind.CSharp, SourceRole.Test,
            ["src/Core/Orisumi.Core/", "src/Backend/Orisumi.Api/"], []),
        new("Orisumi.Dev", "tools/Orisumi.Dev/Orisumi.Dev.csproj", ProjectKind.CSharp, SourceRole.Tooling, [], []),
    ]);

    [Test]
    [Arguments("src/Clients/Orisumi.Web/app/page.tsx", "TypeScript", "Production")]
    [Arguments("src/Clients/Orisumi.Web/tests/page.test.ts", "TypeScript", "Test")]
    [Arguments("tools/Orisumi.Dev/node-tools/comments.mjs", "JavaScript", "Tooling")]
    [Arguments("tests/Orisumi.Model.Tests/Orisumi.Model.Tests.csproj", "MSBuild", "Build")]
    [Arguments("tests/Orisumi.Model.Tests/README.md", "Markdown", "Documentation")]
    [Arguments("src/Clients/Orisumi.Web/pnpm-lock.yaml", "YAML", "Generated")]
    [Arguments("scripts/stray.ts", "TypeScript", "Unclassified")]
    public async Task FilesTakeTheRoleOfTheProjectThatOwnsThemAfterTheirLanguage(string path, string expectedLanguage, string expectedRole)
    {
        await Assert.That(Layout.TryClassify(path, out var language, out var role)).IsTrue();
        await Assert.That(language).IsEqualTo(expectedLanguage);
        await Assert.That(role.ToString()).IsEqualTo(expectedRole);
    }

    [Test]
    public async Task TestProjectsBelongToTheOnlyProductionProjectTheyReference()
    {
        var snapshot = new SourceSnapshot(
        [
            new("src/Core/Orisumi.Core/Observation.cs", "C#", SourceRole.Production, 40),
            new("src/Backend/Orisumi.Api/Program.cs", "C#", SourceRole.Production, 60),
            new("src/Clients/Orisumi.Web/src/app.test.ts", "TypeScript", SourceRole.Test, 7),
            new("tests/Orisumi.Model.Tests/ModelTests.cs", "C#", SourceRole.Test, 20),
            new("tests/Orisumi.Integration.Tests/ApiTests.cs", "C#", SourceRole.Test, 30),
            new("tests/Orisumi.Integration.Tests/Orisumi.Integration.Tests.csproj", "MSBuild", SourceRole.Build, 9),
            new("pnpm-lock.yaml", "YAML", SourceRole.Generated, 500),
        ]);

        var report = LocReport.Create(snapshot, Layout);

        await Assert.That(report.Code).IsEqualTo(new LocRow("Total", 5, 100, 57));
        await Assert.That(report.Modules).IsEquivalentTo(new[]
        {
            new LocRow("Orisumi.Api", 1, 60, 0),
            new LocRow("Orisumi.Core", 2, 40, 20),
            new LocRow("Orisumi.Web", 1, 0, 7),
            new LocRow(LocReport.CrossModule, 1, 0, 30),
        });
        await Assert.That(report.Modules[^1].Name).IsEqualTo(LocReport.CrossModule);
    }

    [Test]
    public async Task HumanOutputScalesBarsToTheLongestRowAndListsUnclassifiedFiles()
    {
        var snapshot = new SourceSnapshot(
        [
            new("src/Core/Orisumi.Core/Observation.cs", "C#", SourceRole.Production, 40),
            new("stray.ts", "TypeScript", SourceRole.Unclassified, 3),
        ]);

        using var writer = new StringWriter();
        LocCommand.Write(Terminal.Create(writer), LocReport.Create(snapshot, Layout));
        var text = writer.ToString();

        await Assert.That(text).Contains("Unclassified        3   1 files");
        await Assert.That(text).Contains("  stray.ts");
        await Assert.That(text).Contains("40        ·   " + new string('█', 30));
        await Assert.That(text).DoesNotContain(new string('█', 31));
    }
}
