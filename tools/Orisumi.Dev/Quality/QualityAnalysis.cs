using System.Text.Json;

namespace Orisumi.Dev;

internal sealed record ErosionMeasurement(int TotalFunctions, int HighRiskFunctions, long TotalBurden, long HighRiskBurden, double Ratio);

internal sealed record ComplexityRegression(ComplexityHotspot Current, int? BaseComplexity);

internal sealed record LineComparison(int Base, int Current)
{
    public int Delta => Current - Base;
}

internal sealed record ComplexityComparison(
    IReadOnlyList<ComplexityHotspot> Hotspots,
    ErosionMeasurement Erosion,
    ErosionMeasurement? BaseErosion,
    IReadOnlyList<ComplexityRegression>? Regressions)
{
    public double? ErosionDelta => BaseErosion is { } before ? Erosion.Ratio - before.Ratio : null;
}

internal sealed record UnusedComparison(IReadOnlyList<DeadCodeFinding> Current, IReadOnlyList<DeadCodeFinding>? New);

internal sealed record QualityReport(
    string Base,
    string Commit,
    LineComparison? ProductionLines,
    ComplexityComparison? Complexity,
    UnusedComparison? Unused,
    IReadOnlyList<string> Failures)
{
    public bool Completed => Failures.Count == 0;

    public int NewFindings => (Complexity?.Regressions?.Count ?? 0) + (Unused?.New?.Count ?? 0);
}

internal sealed class QualityAnalysis(RepositoryContext repository, IProcessRunner runner)
{
    public const int Threshold = 10;

    private sealed record Side(SourceSnapshot Snapshot, CodeScan Code, IReadOnlyList<string> Errors)
    {
        public bool Complete => Errors.Count == 0;
    }

    public async Task<QualityReport> AnalyzeAsync(string requested, string commit, Run run, CancellationToken cancellationToken)
    {
        var layout = ProjectLayout.Load(repository.Root);
        BaselineWorkspace workspace;
        try
        {
            workspace = await BaselineWorkspace.PrepareAsync(repository, commit, run, cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            return await SaveAsync(run, new QualityReport(requested, commit, null, null, null, [exception.Message]));
        }
        var baseline = await ScanAsync(layout, workspace.Path, "baseline", workspace.NeedsRestore, run, cancellationToken);
        if (baseline.Complete) workspace.MarkRestored();
        var current = await ScanAsync(layout, repository.Root, "current", true, run, cancellationToken);
        var report = new QualityReport(
            requested,
            commit,
            new LineComparison(baseline.Snapshot.ProductionLines, current.Snapshot.ProductionLines),
            current.Complete ? CompareComplexity(baseline, current) : null,
            current.Complete ? CompareUnused(baseline, current) : null,
            [.. baseline.Errors.Select(error => $"baseline: {error}"), .. current.Errors.Select(error => $"current: {error}")]);
        return await SaveAsync(run, report);
    }

    private static async Task<QualityReport> SaveAsync(Run run, QualityReport report)
    {
        await run.WriteJsonAsync(RunFiles.Quality, report);
        return report;
    }

    private async Task<Side> ScanAsync(ProjectLayout layout, string root, string label, bool restore, Run run, CancellationToken cancellationToken)
    {
        var snapshot = await SourceSnapshot.ReadAsync(root, layout, cancellationToken);
        var errors = new List<string>();
        var csharp = await Attempt(() => new CSharpAnalysis(repository, runner, layout).ScanAsync(root, label, restore, run, cancellationToken));
        var scripts = await Attempt(() => new ScriptAnalysis(repository, runner, layout).ScanAsync(root, label, snapshot, run, cancellationToken));
        return new Side(snapshot, new CodeScan([.. csharp.Functions, .. scripts.Functions], [.. csharp.Unused, .. scripts.Unused]), errors);

        async Task<CodeScan> Attempt(Func<Task<CodeScan>> scan)
        {
            try
            {
                return await scan();
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException or IOException)
            {
                errors.Add(exception.Message);
                return new CodeScan([], []);
            }
        }
    }

    private static ComplexityComparison CompareComplexity(Side baseline, Side current)
    {
        var hotspots = Hotspots(current.Code.Functions);
        return new ComplexityComparison(
            hotspots,
            Erosion(current.Code.Functions),
            baseline.Complete ? Erosion(baseline.Code.Functions) : null,
            baseline.Complete ? Regressions(Hotspots(baseline.Code.Functions), hotspots) : null);
    }

    private static UnusedComparison CompareUnused(Side baseline, Side current)
    {
        var known = baseline.Code.Unused.Select(item => item.Identity).ToHashSet(StringComparer.Ordinal);
        return new UnusedComparison(current.Code.Unused,
            baseline.Complete ? [.. current.Code.Unused.Where(item => !known.Contains(item.Identity))] : null);
    }

    private static ComplexityHotspot[] Hotspots(IReadOnlyList<FunctionMetric> functions) =>
        [.. functions.Where(function => function.Complexity > Threshold).Select(function => function.Hotspot)];

    internal static IReadOnlyList<ComplexityRegression> Regressions(
        IReadOnlyList<ComplexityHotspot> baseline, IReadOnlyList<ComplexityHotspot> current)
    {
        var previous = baseline.ToDictionary(Identity, item => item.Complexity, StringComparer.Ordinal);
        return [.. current
            .Select(item => new ComplexityRegression(item, previous.TryGetValue(Identity(item), out var complexity) ? complexity : null))
            .Where(item => item.BaseComplexity is null || item.Current.Complexity > item.BaseComplexity)];
    }

    internal static ErosionMeasurement Erosion(IReadOnlyList<FunctionMetric> functions)
    {
        var totalBurden = functions.Sum(item => item.Burden);
        var highRisk = functions.Where(item => item.Complexity > Threshold).ToArray();
        var highRiskBurden = highRisk.Sum(item => item.Burden);
        return new ErosionMeasurement(functions.Count, highRisk.Length, totalBurden, highRiskBurden,
            totalBurden == 0 ? 0 : (double)highRiskBurden / totalBurden);
    }

    private static string Identity(ComplexityHotspot hotspot) => $"{hotspot.Language}\0{hotspot.Path}\0{hotspot.Symbol}";
}
