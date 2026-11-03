using System.Globalization;
using System.Xml.Linq;
using Spectre.Console;

namespace Orisumi.Dev;

internal sealed record TestCase(string Name, bool Passed, double Seconds, string? Message);

internal sealed record TestReport(IReadOnlyList<TestCase>? Cases)
{
    public IEnumerable<TestCase> Failed => (Cases ?? []).Where(test => !test.Passed);
}

internal sealed class TestCheck(string name, ProcessCommand command, ProjectKind kind, IProcessRunner runner, RepositoryContext repository) : ICheck
{
    private const int ListedFailures = 10;

    public string Name => name;

    public string Description => new ProcessCheck(name, command, runner, repository).Description;

    public async Task<CheckOutcome> RunAsync(Run run, CancellationToken cancellationToken)
    {
        var results = run.PathOf(string.Concat(name.Select(character => char.IsLetterOrDigit(character) ? character : '-')) + (kind == ProjectKind.CSharp ? ".trx" : ".junit.xml"));
        var reporting = kind == ProjectKind.CSharp
            ? command with { Arguments = [.. command.Arguments, "--report-trx", "--report-trx-filename", Path.GetFileName(results), "--results-directory", run.Directory] }
            : command with
            {
                Environment = new Dictionary<string, string?>(command.Environment ?? new Dictionary<string, string?>())
                {
                    ["NODE_OPTIONS"] = string.Join(' ', Environment.GetEnvironmentVariable("NODE_OPTIONS") ?? "",
                        "--test-reporter=spec", "--test-reporter-destination=stdout", "--test-reporter=junit", $"--test-reporter-destination={results}").Trim(),
                },
            };
        var outcome = await new ProcessCheck(name, reporting, runner, repository).RunAsync(run, cancellationToken);
        var report = new TestReport(File.Exists(results) ? Parse(await File.ReadAllTextAsync(results, cancellationToken), kind) : null);
        var failed = report.Failed.ToArray();
        var note = report.Cases is not { } cases ? outcome.Note
            : failed.Length > 0 ? Terminal.Verdict(Outcome.Failed, $"{failed.Length} of {cases.Count} failed")
            : $"[grey]{cases.Count} passed[/]";
        return outcome with
        {
            Note = note,
            Report = report,
            Details = console =>
            {
                foreach (var test in failed.Take(ListedFailures))
                    console.MarkupLine($"      [red]{Terminal.Esc(test.Name)}[/]" + (test.Message is { } message ? $"  [grey]{Terminal.Esc(Terminal.Excerpt(message))}[/]" : ""));
                if (failed.Length == 0 || report.Cases is null) outcome.Details(console);
            },
        };
    }

    internal static IReadOnlyList<TestCase> Parse(string xml, ProjectKind kind)
    {
        var document = XDocument.Parse(xml);
        return kind == ProjectKind.CSharp
            ? [.. document.Descendants().Where(element => element.Name.LocalName == "UnitTestResult").Select(result => new TestCase(
                (string?)result.Attribute("testName") ?? "",
                (string?)result.Attribute("outcome") is "Passed" or "NotExecuted",
                TimeSpan.TryParse((string?)result.Attribute("duration"), CultureInfo.InvariantCulture, out var duration) ? duration.TotalSeconds : 0,
                result.Descendants().FirstOrDefault(element => element.Name.LocalName == "Message")?.Value.Trim()))]
            : [.. document.Descendants("testcase").Select(test => new TestCase(
                (string?)test.Attribute("name") ?? "",
                !test.Elements().Any(element => element.Name.LocalName is "failure" or "error"),
                double.TryParse((string?)test.Attribute("time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : 0,
                test.Elements().FirstOrDefault(element => element.Name.LocalName is "failure" or "error")?.Attribute("message")?.Value))];
    }
}
