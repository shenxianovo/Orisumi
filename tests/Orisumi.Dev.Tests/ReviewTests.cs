using System.Text.Json;
using Orisumi.Dev;

namespace Orisumi.Dev.Tests;

public sealed class ReviewTests
{
    private static readonly ReviewRule Rule = new("plain", "正面陈述", ["**/*.md"], "是否违反？", ["违反的例子"], ["符合的例子"]);

    [Test]
    public async Task PassagesSkipFrontmatterFencesHeadingsAndModuleLines()
    {
        var passages = Passages.Split("---\ntitle: x\n---\n\nimport A from 'a';\n\n# 标题\n\n第一段\n第一段续行\n\n```sh\nnot a passage\n```\n\n- 列表\n");
        await Assert.That(string.Join('|', passages.Select(passage => $"{passage.Line}:{passage.Text}")))
            .IsEqualTo("9:第一段\n第一段续行|16:- 列表");
    }

    [Test]
    public async Task AddedPassagesLeaveUnchangedOnesOut()
    {
        var added = Passages.Added("旧段落\n\n改之前", "旧段落\n\n改之后\n\n新段落");
        await Assert.That(string.Join('|', added.Select(passage => passage.Text))).IsEqualTo("改之后|新段落");
    }

    [Test]
    [Arguments("**/*.md", "AGENTS.md", true)]
    [Arguments("**/*.md", "tools/Orisumi.Dev/README.md", true)]
    [Arguments("src/Docs/Orisumi.Docs/content/**", "src/Docs/Orisumi.Docs/content/docs/requirements/belief.mdx", true)]
    [Arguments("*.md", "tools/README.md", false)]
    [Arguments("**/*.md", "AGENTS.mdx", false)]
    public async Task GlobsCrossDirectoriesOnlyWithDoubleStars(string pattern, string path, bool expected) =>
        await Assert.That(Glob.Matches(pattern, path)).IsEqualTo(expected);

    [Test]
    public async Task EveryRuleInTheRepositoryLoadsWithExamplesOnBothSides()
    {
        var repository = await RepositoryContext.DiscoverAsync(AppContext.BaseDirectory);
        var rules = ReviewRules.Load(repository);
        await Assert.That(rules.Count).IsGreaterThan(0);
        await Assert.That(rules.All(rule => rule.Violations.Count > 0 && rule.Conforming.Count > 0)).IsTrue();
    }

    [Test]
    public async Task FindingsComeFromConfidentAnswersAndRepeatedQuestionsUseTheCache()
    {
        using var workspace = new Workspace();
        var client = new FakeClient(input => input.Contains("否定", StringComparison.Ordinal) ? 0.9 : 0.1);
        var target = new ReviewTarget("docs/a.md", [new Passage(3, "一段正常的话"), new Passage(7, "一段否定的话")]);

        var first = await new Reviewer(workspace.Repository, client).ReviewAsync([Rule], [target], [], workspace.Evidence(), CancellationToken.None);
        var second = await new Reviewer(workspace.Repository, client).ReviewAsync([Rule], [target], [], workspace.Evidence(), CancellationToken.None);

        await Assert.That(string.Join('|', first.Findings.Select(finding => $"{finding.Path}:{finding.Line}"))).IsEqualTo("docs/a.md:7");
        await Assert.That(first.Judgments.Count).IsEqualTo(2);
        await Assert.That(client.Calls).IsEqualTo(2);
        await Assert.That(second.Cached).IsEqualTo(2);
        await Assert.That(second.Findings.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(0.9, "符合的例子")]
    [Arguments(0.1, "违反的例子")]
    public async Task CalibrationReportsExamplesTheRuleGetsWrong(double probability, string missed)
    {
        using var workspace = new Workspace();
        var report = await new Reviewer(workspace.Repository, new FakeClient(_ => probability))
            .ReviewAsync([Rule], [], [Rule], workspace.Evidence(), CancellationToken.None);
        await Assert.That(string.Join('|', report.Misses.Select(miss => miss.Example))).IsEqualTo(missed);
    }

    [Test]
    public async Task FailedCallsLeaveTheReviewIncompleteAndUncached()
    {
        using var workspace = new Workspace();
        var target = new ReviewTarget("docs/a.md", [new Passage(1, "一段话")]);
        var report = await new Reviewer(workspace.Repository, new FakeClient(_ => throw new DecisionException("Decisions API returned 500: boom", [])))
            .ReviewAsync([Rule], [target], [], workspace.Evidence(), CancellationToken.None);
        await Assert.That(report.Completed).IsFalse();
        await Assert.That(report.Failures.Single()).Contains("500");
        await Assert.That(Directory.Exists(workspace.Repository.Path(".artifacts", "review-cache"))).IsFalse();
    }

    [Test]
    public async Task RefusalsParseAsMissingProbabilities()
    {
        var answers = OpenAIDecisionClient.Parse("""
            {"answers":[{"type":"predicate","name":"a","probability":0.25},{"type":"refusal","name":"b"}]}
            """);
        await Assert.That(string.Join('|', answers)).IsEqualTo(string.Join('|', new DecisionAnswer("a", 0.25), new DecisionAnswer("b", null)));
    }

    [Test]
    public async Task VerifySkipsReviewWithoutAKeyAndStillPasses()
    {
        using var workspace = new Workspace();
        workspace.WriteRule();
        workspace.Write("docs/guide.md", "新写的一段话");
        var cli = new DeveloperCli(workspace.Repository, new ChangedRunner("docs/guide.md"), workspace.Output, TextWriter.Null,
            new ModelAccess(workspace.Config, _ => throw new InvalidOperationException("no client without a key")));

        await Assert.That(await cli.RunAsync(["verify", "--base", "HEAD"], CancellationToken.None)).IsEqualTo(0);
        await Assert.That(workspace.Output.ToString()).Contains("no OpenAI API key");
        await Assert.That(workspace.Output.ToString()).Contains(ModelAccess.SetupHint);
    }

    [Test]
    public async Task VerifyReportsReviewFindingsWithoutFailing()
    {
        using var workspace = new Workspace();
        workspace.WriteRule();
        workspace.Write("docs/guide.md", "新写的一段话");
        workspace.Config.Set(UserConfig.OpenAIApiKey, "sk-test-0123456789");
        var cli = new DeveloperCli(workspace.Repository, new ChangedRunner("docs/guide.md"), workspace.Output, TextWriter.Null,
            new ModelAccess(workspace.Config, _ => new FakeClient(_ => 0.9)));

        await Assert.That(await cli.RunAsync(["verify", "--base", "HEAD", "--json"], CancellationToken.None)).IsEqualTo(0);
        using var result = JsonDocument.Parse(workspace.Output.ToString());
        var finding = result.RootElement.GetProperty("review").GetProperty("findings").EnumerateArray().Single();
        await Assert.That(finding.GetProperty("path").GetString()).IsEqualTo("docs/guide.md");
    }

    private sealed class FakeClient(Func<string, double> probability) : IDecisionClient
    {
        private int _calls;

        public int Calls => _calls;

        public string Model => "fake";

        public Task<DecisionResult> DecideAsync(string input, IReadOnlyList<DecisionQuestion> questions, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var answers = questions.Select(question => new DecisionAnswer(question.Name, probability(input))).ToArray();
            return Task.FromResult(new DecisionResult(answers, [new DecisionCall("{}", 200, "{}", TimeSpan.Zero)]));
        }
    }

    private sealed class ChangedRunner(string path) : IProcessRunner
    {
        public Task<ProcessResult> CaptureAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken) =>
            Task.FromResult(fileName == "git"
                ? arguments[0] switch
                {
                    "diff" => new ProcessResult(0, path + "\0", ""),
                    "show" => new ProcessResult(128, "", "does not exist"),
                    _ => new ProcessResult(0, "", ""),
                }
                : new ProcessResult(0, "", ""));
    }

    private sealed class Workspace : IDisposable
    {
        public RepositoryContext Repository { get; } = new(Path.Combine(Path.GetTempPath(), "orisumi-review-tests-" + Guid.NewGuid().ToString("N")));
        public StringWriter Output { get; } = new();
        public UserConfig Config { get; }

        public Workspace()
        {
            Directory.CreateDirectory(Repository.Root);
            Config = new UserConfig(Repository.Path("home", "config.json"));
        }

        public string Evidence() => Directory.CreateDirectory(Repository.Path("evidence", Guid.NewGuid().ToString("N"))).FullName;

        public void Write(string path, string text)
        {
            var full = Repository.Path(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        public void WriteRule() => Write(ReviewRules.Directory + "plain.json", JsonSerializer.Serialize(new
        {
            title = Rule.Title, paths = Rule.Paths, question = Rule.Question, violations = Rule.Violations, conforming = Rule.Conforming,
        }));

        public void Dispose()
        {
            Output.Dispose();
            Directory.Delete(Repository.Root, true);
        }
    }
}
