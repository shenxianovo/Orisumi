using Orisumi.Dev;

namespace Orisumi.Dev.Tests;

public sealed class DeveloperCliTests
{
    private const string Docs = "Orisumi.Docs docs:check,Orisumi.Docs test,Orisumi.Docs types:check,Orisumi.Docs build";
    private const string Tool = "dotnet build,dotnet test Orisumi.Dev.Tests";

    [Test]
    [Arguments("src/Docs/Orisumi.Docs/content/docs/start/index.mdx", Docs)]
    [Arguments("AGENTS.md", "Orisumi.Docs docs:check")]
    [Arguments("notes/README.md", "Orisumi.Docs docs:check")]
    [Arguments("tools/Orisumi.Dev/README.md", Tool + ",Orisumi.Docs docs:check")]
    [Arguments("tools/Orisumi.Dev/DeveloperCli.cs", "lint," + Tool)]
    [Arguments("tests/Orisumi.Dev.Tests/TerminalTests.cs", "lint," + Tool)]
    [Arguments("src/Docs/Orisumi.Docs/components/mermaid.tsx", "lint," + Docs)]
    [Arguments(".scratch/model/notes.md", "")]
    [Arguments("unclassified.config", "lint," + Tool + "," + Docs)]
    public async Task ChangedPathsSelectTheChecksOfTheProjectsThatOwnThem(string path, string expected)
    {
        var repository = await RepositoryContext.DiscoverAsync(AppContext.BaseDirectory);
        var planner = new VerificationPlanner(repository, new FixedRunner(0), ModelAccess.Default);
        var (checks, fallback) = planner.ChecksFor(ProjectLayout.Load(repository.Root), [path]);
        await Assert.That(string.Join(',', checks.Select(check => check.Name))).IsEqualTo(expected);
        await Assert.That(fallback).IsEqualTo(path == "unclassified.config");
    }

    [Test]
    public async Task TheSolutionListsProjectsOfEveryLanguageWithTheRoleOfTheirFolder()
    {
        using var repository = new TestRepository();
        repository.Write("Orisumi.slnx", TestRepository.Solution.Replace("<Folder Name=\"/tools/\">", """
            <Folder Name="/src/">
                <File Path="src/Web/package.json" />
                <File Path="src/Web/README.md" />
              </Folder>
              <Folder Name="/tools/">
            """, StringComparison.Ordinal));
        repository.Write("src/Web/package.json", """{ "scripts": { "test": "node --test", "dev": "next dev" } }""");
        repository.Write("src/Web/pnpm-lock.yaml", "");

        var layout = ProjectLayout.Load(repository.Context.Root);

        await Assert.That(string.Join(',', layout.Projects.Select(project => $"{project.Name}:{project.Kind}:{project.Role}"))).IsEqualTo(
            "Example.Tests:CSharp:Test,Web:Node:Production,Example:CSharp:Tooling");
        await Assert.That(layout.Owner("src/Web/app/page.tsx")!.PackageManager).IsEqualTo("pnpm");
        await Assert.That(layout.Owner("src/Web/app/page.tsx")!.Scripts).IsEquivalentTo(["test", "dev"]);
        await Assert.That(layout.TestsCovering(layout.Owner("tools/Example/Example.cs")!).Single().Name).IsEqualTo("Example.Tests");
    }

    [Test]
    public async Task ProjectsOutsideTheKnownFoldersAreRejected()
    {
        using var repository = new TestRepository();
        repository.Write("Orisumi.slnx", TestRepository.Solution.Replace("/tools/", "/misc/", StringComparison.Ordinal));
        await Assert.That(() => ProjectLayout.Load(repository.Context.Root)).Throws<InvalidOperationException>();
    }
}
