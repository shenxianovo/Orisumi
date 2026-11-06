namespace Orisumi.Dev.Tests;

public class FormatterTests
{
    [Test]
    public async Task FormatterFailureDoesNotPassAsClean()
    {
        using var sample = new FormatSample();
        var formatter = sample.Formatter(new ProcessResult(17, "", "simulated formatter failure"));
        await Assert.That(async () => { await formatter.CheckAsync(["Sample.cs"], CancellationToken.None); }).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task FormattingDiagnosticsRemainFindings()
    {
        using var sample = new FormatSample();
        var formatter = sample.Formatter(new ProcessResult(2, $"{sample.File}(1,1): error WHITESPACE: Fix whitespace formatting.\n", ""));
        var findings = await formatter.CheckAsync(["Sample.cs"], CancellationToken.None);
        await Assert.That(findings.SequenceEqual(["Sample.cs"])).IsTrue();
    }

    [Test]
    public async Task SuccessfulFormatterWithoutDiagnosticsIsClean()
    {
        using var sample = new FormatSample();
        var formatter = sample.Formatter(new ProcessResult(0, "", ""));
        var findings = await formatter.CheckAsync(["Sample.cs"], CancellationToken.None);
        await Assert.That(findings.Count).IsEqualTo(0);
    }

    private sealed class FormatSample : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "orisumi-dev-tests", Guid.NewGuid().ToString("N"));

        public FormatSample()
        {
            Directory.CreateDirectory(_directory);
            System.IO.File.WriteAllText(File, "class Sample { }\n");
        }

        public string File => Path.Combine(_directory, "Sample.cs");

        public Formatter Formatter(ProcessResult result) => new(new RepositoryContext(_directory), new StubRunner(result));

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    private sealed class StubRunner(ProcessResult result) : IProcessRunner
    {
        public Task<ProcessResult> CaptureAsync(ProcessCommand command, CancellationToken cancellationToken) => Task.FromResult(result);
    }
}
