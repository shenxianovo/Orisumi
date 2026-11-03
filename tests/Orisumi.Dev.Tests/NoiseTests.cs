using System.Text.Json;
using Orisumi.Dev;

namespace Orisumi.Dev.Tests;

public sealed class NoiseTests
{
    private static readonly ReviewRule Css = new("css", "代码之外的引用", RuleKind.Noise, ["knip/*"], "是否在 CSS 里引用？", ["是"], ["否"]);
    private static readonly ReviewRule Entry = new("entry", "框架约定的入口", RuleKind.Noise, ["knip/*"], "是否是约定入口？", ["是"], ["否"]);
    private static readonly QualityFinding Tailwind = new("knip/devDependencies", "web/package.json", 31, "tailwindcss");

    [Test]
    [Arguments(null, 0.95, 0.2, "Explained", "css")]
    [Arguments(null, 0.3, 0.2, "Unexplained", null)]
    [Arguments("css", 0.95, 0.2, "Declared", "css")]
    [Arguments("entry", 0.95, 0.2, "Contradicted", "css")]
    public async Task TheMostLikelyReasonAboveTheThresholdExplainsAFindingAndDeclarationsMustStillHold(
        string? declared, double css, double entry, string status, string? reason)
    {
        var judging = new Judging(new Dictionary<(string, string), double?> { [("evidence", "css")] = css, [("evidence", "entry")] = entry }, 0, [], []);
        var judgment = NoiseJudge.Classify(Tailwind, "evidence", [Css, Entry], declared, judging);
        await Assert.That(judgment.Status.ToString()).IsEqualTo(status);
        await Assert.That(judgment.Reason).IsEqualTo(reason);
    }

    [Test]
    public async Task DeclarationsStandWhenTheModelGaveNoAnswer()
    {
        var judging = new Judging(new Dictionary<(string, string), double?>(), 0, ["Decisions API unreachable"], []);
        await Assert.That(NoiseJudge.Classify(Tailwind, "evidence", [Css], "css", judging).Status).IsEqualTo(NoiseStatus.Declared);
        await Assert.That(NoiseJudge.Classify(Tailwind, "evidence", [Css], "css", null).Status).IsEqualTo(NoiseStatus.Declared);
    }

    [Test]
    public async Task EvidenceListsRealReferencesAndLockfileEntriesWhereThePackageIsTheKey()
    {
        using var repository = Web();
        var evidence = new NoiseJudge(repository.Context, NoKey(repository), ProjectLayout.Load(repository.Context.Root))
            .Evidence(Tailwind, Sources());
        await Assert.That(evidence).Contains("web/app/global.css:1: @import 'tailwindcss';");
        await Assert.That(evidence).DoesNotContain("postcss.config.mjs");
        await Assert.That(evidence).Contains("importers > . > devDependencies > tailwindcss:");
        await Assert.That(evidence).DoesNotContain("theme@1.0.0");
    }

    [Test]
    public async Task ProjectsDeclareTheirNoiseAndDeclarationsWithoutAFindingFailTheScan()
    {
        using var repository = Web();
        repository.Write("web/noise.json", JsonSerializer.Serialize(new object[]
        {
            new { rule = "knip/devDependencies", path = "package.json", symbol = "tailwindcss", reason = "css" },
            new { rule = "knip/dependencies", path = "package.json", symbol = "lodash", reason = "css" },
            new { rule = "knip/dependencies", path = "package.json", symbol = "left-pad", reason = "unknown" },
        }));
        var run = new RunStore(repository.Context).Create("analyze quality");

        var (report, failures) = await new NoiseJudge(repository.Context, NoKey(repository), ProjectLayout.Load(repository.Context.Root))
            .JudgeAsync([Tailwind], Sources(), run, CancellationToken.None);

        await Assert.That(report.Judged).IsFalse();
        await Assert.That(report.IsFolded(Tailwind)).IsTrue();
        await Assert.That(report.Stale.Single().Symbol).IsEqualTo("lodash");
        await Assert.That(failures.Count).IsEqualTo(2);
        await Assert.That(failures.Any(failure => failure.Contains("unknown reason 'unknown'", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task FoldedFindingsLeaveTheCountsAndTheNewFindings()
    {
        var finding = new DeadCodeFinding("knip/devDependencies", "web/package.json", 31, "tailwindcss");
        var noise = new NoiseReport(true, [new NoiseJudgment(Tailwind, NoiseStatus.Declared, "css", "代码之外的引用", 0.95, "css", "")], []);
        var report = new QualityReport("HEAD", "abc", null, null, new UnusedComparison([finding], [finding]), noise, []);
        await Assert.That(report.UnusedCode.Count()).IsEqualTo(0);
        await Assert.That(report.NewFindings).IsEqualTo(0);
    }

    private static TestRepository Web()
    {
        var repository = new TestRepository(withProjects: false);
        repository.Write("Orisumi.slnx", """<Solution><Folder Name="/src/"><File Path="web/package.json" /></Folder></Solution>""");
        repository.Write("web/package.json", """{ "devDependencies": { "tailwindcss": "^4.3.3" } }""");
        repository.Write("web/app/global.css", "@import 'tailwindcss';\n");
        repository.Write("web/postcss.config.mjs", "export default { plugins: { '@tailwindcss/postcss': {} } };\n");
        repository.Write("web/pnpm-lock.yaml", "importers:\n  .:\n    devDependencies:\n      tailwindcss:\n        specifier: ^4.3.3\nsnapshots:\n  theme@1.0.0(tailwindcss@4.3.3):\n    dependencies: {}\n");
        repository.Write(ReviewRules.NoiseDirectory + "css.json", JsonSerializer.Serialize(new
        {
            title = Css.Title, findings = Css.Covers, question = Css.Question, yes = Css.Yes, no = Css.No,
        }));
        return repository;
    }

    private static NoiseSources Sources() =>
        new(["web/package.json", "web/app/global.css", "web/postcss.config.mjs", "web/pnpm-lock.yaml"], [], []);

    private static ModelAccess NoKey(TestRepository repository) =>
        new(new UserConfig(repository.Context.Path(".artifacts", "home", "config.json")), _ => throw new InvalidOperationException("no key"));
}
