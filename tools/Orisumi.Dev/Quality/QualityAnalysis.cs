using System.Text.Json;

namespace Orisumi.Dev;

internal sealed record ErosionMeasurement(int TotalFunctions, int HighRiskFunctions, long TotalBurden, long HighRiskBurden, double Ratio);

internal sealed record ComplexityRegression(ComplexityHotspot Current, int? BaseComplexity);

internal sealed record LineComparison(int Base, int Current)
{
    public int Delta => Current - Base;
}

internal sealed record VanishedFunction(string Path, string Symbol, int Complexity, string Source);

internal sealed record ComplexityReport(
    LineComparison? ProductionLines,
    IReadOnlyList<ComplexityHotspot> Hotspots,
    ErosionMeasurement? Erosion,
    ErosionMeasurement? BaseErosion,
    IReadOnlyList<ComplexityRegression>? Regressions,
    IReadOnlyList<string> Failures)
{
    public double? ErosionDelta => Erosion is { } now && BaseErosion is { } before ? now.Ratio - before.Ratio : null;

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<FunctionMetric> Functions { get; init; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<VanishedFunction> Vanished { get; init; } = [];
}

internal sealed class QualityAnalysis(RepositoryContext repository, IProcessRunner runner)
{
    public const int Threshold = 10;
    private const int MaxSourceLines = 120;

    private sealed record Side(SourceSnapshot Snapshot, IReadOnlyList<FunctionMetric> Functions, IReadOnlyList<string> Errors)
    {
        public bool Complete => Errors.Count == 0;
    }

    public async Task<ComplexityReport> AnalyzeAsync(string commit, Run run, CancellationToken cancellationToken)
    {
        var layout = ProjectLayout.Load(repository.Root);
        BaselineWorkspace workspace;
        try
        {
            workspace = await BaselineWorkspace.PrepareAsync(repository, commit, run, cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            return new ComplexityReport(null, [], null, null, null, [exception.Message]);
        }
        var baseline = await ScanAsync(layout, workspace.Path, "baseline", workspace.NeedsRestore, run, cancellationToken);
        if (baseline.Complete) workspace.MarkRestored();
        var current = await ScanAsync(layout, repository.Root, "current", true, run, cancellationToken);
        List<string> failures = [.. baseline.Errors.Select(error => $"baseline: {error}"), .. current.Errors.Select(error => $"current: {error}")];
        if (!current.Complete) return new ComplexityReport(null, [], null, null, null, failures);
        var hotspots = Hotspots(current.Functions);
        return new ComplexityReport(
            new LineComparison(baseline.Snapshot.ProductionLines, current.Snapshot.ProductionLines),
            hotspots,
            Erosion(current.Functions),
            baseline.Complete ? Erosion(baseline.Functions) : null,
            baseline.Complete ? Regressions(Hotspots(baseline.Functions), hotspots) : null,
            failures)
        {
            Functions = current.Functions,
            Vanished = baseline.Complete ? Vanished(workspace.Path, baseline.Functions, current.Functions) : [],
        };
    }

    private async Task<Side> ScanAsync(ProjectLayout layout, string root, string label, bool restore, Run run, CancellationToken cancellationToken)
    {
        var snapshot = await SourceSnapshot.ReadAsync(root, layout, cancellationToken);
        var errors = new List<string>();
        var csharp = await Attempt(() => new CSharpAnalysis(repository, runner, layout).ScanAsync(root, label, restore, run, cancellationToken));
        var scripts = await Attempt(() => new ScriptAnalysis(repository, runner, layout).ScanAsync(root, label, snapshot, run, cancellationToken));
        return new Side(snapshot, [.. csharp, .. scripts], errors);

        async Task<IReadOnlyList<FunctionMetric>> Attempt(Func<Task<IReadOnlyList<FunctionMetric>>> scan)
        {
            try
            {
                return await scan();
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException or IOException)
            {
                errors.Add(exception.Message);
                return [];
            }
        }
    }

    internal static string Source(string[] lines, FunctionMetric function) =>
        string.Join('\n', lines.Skip(function.Line - 1).Take(Math.Clamp(function.EndLine - function.Line + 1, 1, MaxSourceLines)));

    private static VanishedFunction[] Vanished(string root, IReadOnlyList<FunctionMetric> baseline, IReadOnlyList<FunctionMetric> current)
    {
        var known = Hotspots(current).Select(Identity).ToHashSet(StringComparer.Ordinal);
        return [.. baseline
            .Where(function => function.Complexity > Threshold && !known.Contains(Identity(function.Hotspot)))
            .Select(function => new VanishedFunction(function.Path, ShortSymbol(function.Symbol), function.Complexity,
                Source(File.ReadAllLines(Path.Combine(root, function.Path)), function)))];
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

    internal static string ShortSymbol(string symbol)
    {
        var segments = System.Text.RegularExpressions.Regex.Replace(symbol, @"\(.*\)", "").Split('/')
            .Select(segment => System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(segment, "^[A-Za-z]+:", ""), @"`\d+", ""))
            .Where(segment => segment.Length > 0)
            .ToArray();
        return string.Join('.', segments.TakeLast(2));
    }

    private static string Identity(ComplexityHotspot hotspot) => $"{hotspot.Language}\0{hotspot.Path}\0{hotspot.Symbol}";
}
