using System.CommandLine;
using System.Diagnostics;
using Spectre.Console;

namespace Orisumi.Dev;

internal sealed class VerificationCommand(
    RepositoryContext repository,
    IProcessRunner runner,
    TextWriter output,
    TextWriter error,
    ModelAccess models)
{
    public Command CreateCommand()
    {
        var command = new Command("verify", "Build, test, lint and review; the scope selects checks by changed paths or runs them all");
        var scope = new Option<string>("--scope")
        {
            Description = "changed selects checks by changed paths; all runs every check except quality",
            DefaultValueFactory = _ => "changed",
        };
        scope.AcceptOnlyFromAmong("changed", "all");
        var baseRef = new Option<string?>("--base") { Description = "Git comparison base for changed scope" };
        var quality = new Option<bool>("--quality") { Description = "Also analyze quality against the same base (changed scope only)" };
        var plan = new Option<bool>("--plan") { Description = "Print the plan without executing it" };
        var json = CommandLine.Json();
        command.Options.Add(scope);
        command.Options.Add(baseRef);
        command.Options.Add(quality);
        command.Options.Add(plan);
        command.Options.Add(json);
        command.SetAction((parse, token) => RunAsync(new VerificationRequest(
            parse.GetValue(scope) == "all" ? VerificationScope.All : VerificationScope.Changed, parse.GetValue(baseRef), parse.GetValue(plan), parse.GetValue(json), parse.GetValue(quality)), token));
        return command;
    }

    public async Task<int> RunAsync(VerificationRequest request, CancellationToken cancellationToken)
    {
        var plan = await new VerificationPlanner(repository, runner, models).CreateAsync(request, cancellationToken);
        if (request.PlanOnly)
        {
            await WritePlanAsync(plan, request.Json);
            return 0;
        }
        var progress = request.Json ? error : output;
        var console = Terminal.Create(progress);
        WriteHeading(console, plan, "Verify");
        VerificationRecord? record = null;
        var clock = Stopwatch.StartNew();
        try
        {
            return await Run.ExecuteAsync(repository, plan.Title, async run =>
            {
                var results = new List<CheckRecord>();
                try
                {
                    await RunChecksAsync(console, plan, run, results, cancellationToken);
                }
                finally
                {
                    record = new VerificationRecord(run.Directory, plan.ScopeName, plan.Fallback, plan.Base, plan.ChangedPaths,
                        ExitCode(results), results);
                    await run.WriteJsonAsync(RunFiles.Verification, record);
                }
                return record.ExitCode;
            }, notes: progress);
        }
        finally
        {
            if (record is not null)
            {
                WriteVerdict(console, record, clock.Elapsed);
                if (request.Json) await CommandLine.WriteJsonAsync(output, record);
            }
        }
    }

    private static async Task RunChecksAsync(
        IAnsiConsole console, VerificationPlan plan, Run run, List<CheckRecord> results, CancellationToken cancellationToken)
    {
        var width = NameWidth(plan);
        foreach (var check in plan.Checks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clock = Stopwatch.StartNew();
            CheckOutcome outcome;
            try
            {
                outcome = await Terminal.SpinAsync(console, $"Running {Terminal.Esc(check.Name)}", () => check.RunAsync(run, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                results.Add(new CheckRecord(check.Name, 130, LogOf(run, check), null));
                WriteStep(console, check.Name, width, Outcome.Cancelled, clock.Elapsed);
                throw;
            }
            results.Add(new CheckRecord(check.Name, outcome.ExitCode, LogOf(run, check), outcome.Report));
            WriteStep(console, check.Name, width, outcome.Mark, clock.Elapsed, outcome.Note);
            outcome.Details(console);
            if (outcome.ExitCode == 130) return;
        }
    }

    private static string? LogOf(Run run, ICheck check) => run.Log(check.Name) is var log && File.Exists(log) ? log : null;

    internal static int ExitCode(IReadOnlyList<CheckRecord> results) =>
        results.Any(result => result.ExitCode == 130) ? 130
        : results.FirstOrDefault(result => result.ExitCode != 0)?.ExitCode ?? 0;

    private async Task WritePlanAsync(VerificationPlan plan, bool json)
    {
        if (json)
        {
            await CommandLine.WriteJsonAsync(output, new
            {
                scope = plan.ScopeName,
                plan.Fallback,
                plan.Base,
                plan.ChangedPaths,
                checks = plan.Checks.Select(check => new { check.Name, check.Description }),
            });
            return;
        }
        var console = Terminal.Create(output);
        WriteHeading(console, plan, "Verify plan");
        var width = NameWidth(plan);
        if (plan.Checks.Count == 0) console.MarkupLine($"  {Terminal.Mark(Outcome.Skipped)} No checks selected");
        Terminal.Rows(console, plan.Checks.Select(check => new[]
        {
            Terminal.Esc(check.Name.PadRight(width)),
            $"[grey]{Terminal.Esc(check.Description)}[/]",
        }));
    }

    private static void WriteHeading(IAnsiConsole console, VerificationPlan plan, string subject)
    {
        var scope = plan switch
        {
            { Scope: VerificationScope.All } => "all checks",
            { Fallback: true } => $"all checks; changes since {Terminal.ShortRevision(plan.Base)} reach paths outside every project",
            _ => $"changed since {Terminal.ShortRevision(plan.Base)}",
        };
        Terminal.Heading(console, subject, Terminal.Esc(scope), $"{plan.Checks.Count} {(plan.Checks.Count == 1 ? "check" : "checks")}");
    }

    private static int NameWidth(VerificationPlan plan) => plan.Checks.Select(check => check.Name.Length).DefaultIfEmpty(0).Max();

    private static void WriteStep(IAnsiConsole console, string name, int width, Outcome outcome, TimeSpan elapsed, string note = "") =>
        console.MarkupLine($"  {Terminal.Mark(outcome)} {Terminal.Esc(name.PadRight(width))}  [grey]{Terminal.Duration(elapsed),6}[/]"
            + (note.Length > 0 ? "  " + note : ""));

    private void WriteVerdict(IAnsiConsole console, VerificationRecord record, TimeSpan elapsed)
    {
        var outcome = Terminal.FromExitCode(record.ExitCode);
        var text = outcome switch
        {
            Outcome.Passed => "Passed",
            Outcome.Cancelled => "Cancelled",
            _ => "Failed",
        };
        console.WriteLine();
        console.MarkupLine($"{Terminal.Mark(outcome)} {Terminal.Verdict(outcome, $"{text} in {Terminal.Duration(elapsed)}")}");
        console.MarkupLine($"  [grey]evidence[/]  {Terminal.Esc(Terminal.Path(repository, record.Evidence))}/");
    }
}
