using System.Text.Json;
using Spectre.Console;

namespace Orisumi.Dev;

internal sealed record VerificationRequest(string Mode, string? Base, bool PlanOnly, bool Json, bool Quality = false)
{
    public async Task<VerificationRequest> ResolveAsync(IProcessRunner runner, CancellationToken cancellationToken)
    {
        if (Mode == "full" && (Base is not null || Quality))
            throw new CommandUsageException("--base and --quality require --scope changed.");
        var baseRef = await ResolveBaseAsync(Mode, Base, runner, cancellationToken);
        if (Quality && baseRef is not null) baseRef = await ResolveCommitAsync(baseRef, runner, cancellationToken);
        return this with { Base = baseRef };
    }

    internal static async Task<string?> ResolveBaseAsync(
        string mode,
        string? baseRef,
        IProcessRunner runner,
        CancellationToken cancellationToken)
    {
        if (mode != "changed" || baseRef is not null) return baseRef;
        var status = await runner.CaptureAsync("git", ["status", "--porcelain"], null, cancellationToken);
        if (status.ExitCode != 0) throw new InvalidOperationException(status.StdErr.Trim());
        if (string.IsNullOrWhiteSpace(status.StdOut))
            throw new CommandUsageException("Changed scope needs --base when the worktree is clean.");
        return "HEAD";
    }

    private static async Task<string> ResolveCommitAsync(string baseRef, IProcessRunner runner, CancellationToken cancellationToken)
    {
        var resolved = await runner.CaptureAsync("git", ["rev-parse", "--verify", $"{baseRef}^{{commit}}"], null, cancellationToken);
        if (resolved.ExitCode != 0) throw new CommandUsageException($"Invalid Git base '{baseRef}'.");
        return resolved.StdOut.Trim();
    }
}

internal sealed record VerificationStep(
    string Name, string FileName, IReadOnlyList<string> Arguments,
    Func<TextWriter, CancellationToken, Task<int>>? InProcess = null);

internal sealed record VerificationPlan(
    string Mode, string? Base, IReadOnlyList<string> ChangedPaths, IReadOnlyList<VerificationStep> Steps,
    IReadOnlyList<string>? ReviewPaths = null)
{
    public IReadOnlyList<string> ReviewPaths { get; } = ReviewPaths ?? [];
}

internal static class VerificationPlanner
{
    public static async Task<VerificationPlan> CreateAsync(
        RepositoryContext repository, IProcessRunner runner, VerificationRequest request, CancellationToken cancellationToken)
    {
        if (request.Mode == "full") return new("full", request.Base, [], Full(repository, runner));
        var paths = await ChangedPathsAsync(runner, request.Base!, cancellationToken);
        var rules = ReviewRules.Load(repository);
        var review = paths.Where(path => ReviewSelection.Reviewable(path, rules) || ReviewRules.IdOf(path) is not null).ToArray();
        var selection = paths.Aggregate(CheckSelection.None, (current, path) => current | Select(path));
        if (selection.HasFlag(CheckSelection.Unknown)) return new("full-fallback", request.Base, paths, Full(repository, runner), review);
        var steps = new List<VerificationStep>();
        if (paths.Any(CommentLint.Covers)) steps.Add(Lint(repository, runner));
        if (selection.HasFlag(CheckSelection.Build)) steps.Add(Build(repository));
        if (selection.HasFlag(CheckSelection.Cli)) steps.Add(CliTests(repository));
        if (selection.HasFlag(CheckSelection.Docs)) steps.AddRange(Docs(repository));
        else if (selection.HasFlag(CheckSelection.DocsCheck)) steps.Add(DocsCheck(repository));
        return new("changed", request.Base, paths, steps, review);
    }

    private static CheckSelection Select(string path)
    {
        var selection = SelectByArea(path);
        var name = Path.GetFileName(path);
        var isProjectDocument = name is "README.md" or "AGENTS.md"
            || (path.StartsWith(".agents/", StringComparison.Ordinal) && path.EndsWith(".md", StringComparison.Ordinal));
        return isProjectDocument ? (selection & ~CheckSelection.Unknown) | CheckSelection.DocsCheck : selection;
    }

    private static readonly (string[] Prefixes, CheckSelection Selection)[] Areas =
    [
        (["src/Docs/Orisumi.Docs/"], CheckSelection.Docs),
        (["tools/Orisumi.Dev/", "tests/Orisumi.Dev.Tests/"], CheckSelection.Build | CheckSelection.Cli),
        ([".scratch/"], CheckSelection.None),
    ];

    private static CheckSelection SelectByArea(string path)
    {
        if (path is "README.md" or "AGENTS.md") return CheckSelection.None;
        foreach (var (prefixes, selection) in Areas)
        {
            if (prefixes.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal))) return selection;
        }
        return CheckSelection.Unknown;
    }

    private static VerificationStep Build(RepositoryContext repository) =>
        new("dotnet-build", "dotnet", ["build", repository.Path("Orisumi.slnx"), "--verbosity", "minimal"]);

    private static VerificationStep CliTests(RepositoryContext repository) =>
        new("developer-cli-tests", "dotnet", ["test", "--project",
            repository.Path("tests", "Orisumi.Dev.Tests", "Orisumi.Dev.Tests.csproj")]);

    private static VerificationStep DocsCheck(RepositoryContext repository) =>
        new("docs-check", "pnpm", ["--dir", repository.Path("src", "Docs", "Orisumi.Docs"), "docs:check"]);

    private static IReadOnlyList<VerificationStep> Docs(RepositoryContext repository)
    {
        var root = repository.Path("src", "Docs", "Orisumi.Docs");
        return [
            DocsCheck(repository),
            new("docs-test", "pnpm", ["--dir", root, "test"]),
            new("docs-types", "pnpm", ["--dir", root, "types:check"]),
            new("docs-build", "pnpm", ["--dir", root, "build"]),
        ];
    }

    private static IReadOnlyList<VerificationStep> Full(RepositoryContext repository, IProcessRunner runner) =>
        [Lint(repository, runner), Build(repository), CliTests(repository), .. Docs(repository)];

    private static VerificationStep Lint(RepositoryContext repository, IProcessRunner runner) =>
        new("lint", "orisumi", ["lint"], (output, token) => new LintCommand(repository, runner, output).RunAsync(false, token));

    internal static async Task<IReadOnlyList<string>> ChangedPathsAsync(
        IProcessRunner runner, string baseRef, CancellationToken cancellationToken)
    {
        var tracked = await runner.CaptureAsync("git",
            ["diff", "--no-renames", "--name-only", "-z", baseRef, "--"], null, cancellationToken);
        if (tracked.ExitCode != 0) throw new CommandUsageException($"Invalid Git base '{baseRef}': {tracked.StdErr.Trim()}");
        var untracked = await runner.CaptureAsync("git",
            ["ls-files", "--others", "--exclude-standard", "-z"], null, cancellationToken);
        if (untracked.ExitCode != 0) throw new InvalidOperationException(untracked.StdErr.Trim());
        return tracked.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Concat(untracked.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    [Flags]
    private enum CheckSelection { None = 0, Build = 1, Cli = 2, Docs = 4, DocsCheck = 8, Unknown = 16 }
}

internal static class VerificationReporter
{
    public static async Task WriteAsync(
        RepositoryContext repository, TextWriter output, VerificationPlan plan, bool json, bool quality = false)
    {
        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                plan.Mode,
                quality,
                plan.Base,
                plan.ChangedPaths,
                steps = plan.Steps.Select(step => new { step.Name, step.FileName, step.Arguments }),
                plan.ReviewPaths,
            }, JsonOptions.Indented));
            return;
        }
        var console = Terminal.Create(output);
        WriteHeading(console, plan, quality, "Verify plan");
        var width = NameWidth(plan);
        var rows = plan.Steps.Select(step => new[]
        {
            Terminal.Esc(step.Name.PadRight(width)),
            $"[grey]{Terminal.Esc(string.Join(' ', [step.FileName, .. step.Arguments.Select(argument => Relative(repository, argument))]))}[/]",
        }).ToList();
        if (quality) rows.Add(["quality".PadRight(width), $"[grey]structural observations against {Terminal.Esc(ShortBase(plan.Base))}[/]"]);
        if (plan.ReviewPaths.Count > 0)
            rows.Add(["review".PadRight(width), $"[grey]model-judged rules on {plan.ReviewPaths.Count} changed {(plan.ReviewPaths.Count == 1 ? "file" : "files")}[/]"]);
        if (rows.Count == 0) console.MarkupLine($"  {Terminal.Mark(Outcome.Skipped)} No executable checks selected");
        Terminal.Rows(console, rows);
    }

    public static void WriteHeading(IAnsiConsole console, VerificationPlan plan, bool quality, string subject = "Verify")
    {
        var scope = plan.Mode switch
        {
            "changed" => $"changed since {ShortBase(plan.Base)}",
            "full-fallback" => $"all checks; changes since {ShortBase(plan.Base)} reach unmapped paths",
            _ => "all checks",
        };
        var count = $"{plan.Steps.Count} {(plan.Steps.Count == 1 ? "check" : "checks")}" + (quality ? " + quality" : "")
            + (plan.ReviewPaths.Count > 0 ? " + review" : "");
        Terminal.Heading(console, subject, Terminal.Esc(scope), count);
    }

    public static int NameWidth(VerificationPlan plan) =>
        plan.Steps.Select(step => step.Name.Length).Append("quality".Length).Max();

    public static void WriteStep(IAnsiConsole console, string name, int width, Outcome outcome, TimeSpan elapsed, string note = "") =>
        console.MarkupLine($"  {Terminal.Mark(outcome)} {Terminal.Esc(name.PadRight(width))}  [grey]{Terminal.Duration(elapsed),6}[/]"
            + (note.Length > 0 ? "  " + note : ""));

    public static void WriteVerdict(IAnsiConsole console, RepositoryContext repository, int exitCode, TimeSpan elapsed, string evidence)
    {
        var outcome = Terminal.FromExitCode(exitCode);
        var text = outcome switch
        {
            Outcome.Passed => "Passed",
            Outcome.Cancelled => "Cancelled",
            _ => "Failed",
        };
        console.WriteLine();
        console.MarkupLine($"{Terminal.Mark(outcome)} {Terminal.Verdict(outcome, $"{text} in {Terminal.Duration(elapsed)}")}");
        console.MarkupLine($"  [grey]evidence[/]  {Terminal.Esc(Terminal.Path(repository, evidence))}/");
    }

    private static string ShortBase(string? value) =>
        value is { Length: 40 } && value.All(char.IsAsciiHexDigit) ? value[..7] : value ?? "";

    private static string Relative(RepositoryContext repository, string argument) =>
        Path.IsPathRooted(argument) && argument.StartsWith(repository.Root, StringComparison.Ordinal)
            ? Terminal.Path(repository, argument) : argument;
}
