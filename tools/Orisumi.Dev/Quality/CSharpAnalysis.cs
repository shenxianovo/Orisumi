using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Orisumi.Dev;

internal sealed record ComplexityHotspot(string Language, string Path, int Line, string Symbol, int Complexity, int Column = 1);


internal sealed partial class CSharpAnalysis(RepositoryContext repository, IProcessRunner runner, ProjectLayout layout)
{
    public async Task<IReadOnlyList<FunctionMetric>> ScanAsync(string root, string label, bool restore, Run run, CancellationToken cancellationToken)
    {
        var solution = Prepare(root, layout, run.PathOf(label + "-implementation.slnx"));
        if (solution.Projects == 0) return [];
        List<string> arguments =
        [
            "build", solution.Path, "--no-incremental", "--disable-build-servers", "--verbosity", "minimal", "--maxcpucount:1",
            "-p:TreatWarningsAsErrors=false", "-p:WarningsAsErrors=",
            $"-p:CustomBeforeMicrosoftCommonProps={repository.Path("tools", "Orisumi.Dev", "CodeMetrics.props")}",
        ];
        if (!restore) arguments.Add("--no-restore");
        var command = new ProcessCommand("dotnet", arguments, root,
            new Dictionary<string, string?> { ["DOTNET_CLI_UI_LANGUAGE"] = "en-US", ["VSLANG"] = "1033" });
        run.Commands.Add($"{command} (C# implementation metrics)");
        var log = run.PathOf(label + "-csharp.log");
        ProcessResult result;
        try
        {
            result = await runner.CaptureAsync(command, cancellationToken);
        }
        catch (CapturedProcessCancelledException cancelled)
        {
            await File.WriteAllTextAsync(log, cancelled.StdOut + cancelled.StdErr, CancellationToken.None);
            throw;
        }
        var output = result.StdOut + Environment.NewLine + result.StdErr;
        await File.WriteAllTextAsync(log, output, cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidDataException($"C# build failed with exit code {result.ExitCode}: {FailureSummary(result)}");
        var measured = ParseComplexity(root, output);
        if (measured.Count == 0)
            throw new InvalidDataException("CA1502 produced no function metrics; check that tools/Orisumi.Dev/CodeMetrics.props enables it.");
        return ErosionScanner.FromCSharpAnalyzer(root, [.. measured.Where(item => layout.IsImplementation(item.Path))]);
    }

    internal static (string Path, int Projects) Prepare(string root, ProjectLayout layout, string outputSolution)
    {
        var solution = XDocument.Load(Directory.EnumerateFiles(root, "*.slnx").Single());
        var implementation = layout.Projects.Where(project => project.IsImplementation).Select(project => project.Manifest).ToHashSet(StringComparer.Ordinal);
        foreach (var file in solution.Descendants("File").ToArray()) file.Remove();
        foreach (var project in solution.Descendants("Project").ToArray())
        {
            var relative = project.Attribute("Path")!.Value.Replace('\\', '/');
            if (!implementation.Contains(relative)) project.Remove();
            else project.SetAttributeValue("Path", System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative)));
        }
        solution.Save(outputSolution);
        return (outputSolution, solution.Descendants("Project").Count());
    }

    internal static IReadOnlyList<ComplexityHotspot> ParseComplexity(string root, string output) =>
        [.. output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => ComplexityDiagnostic().Match(line))
            .Where(match => match.Success)
            .Select(match => new ComplexityHotspot(
                "C#", Relative(root, match.Groups["path"].Value),
                int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture),
                match.Groups["symbol"].Value,
                int.Parse(match.Groups["complexity"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["column"].Value, CultureInfo.InvariantCulture)))
            .Distinct()
            .OrderBy(hotspot => hotspot.Path, StringComparer.Ordinal)
            .ThenBy(hotspot => hotspot.Line)];

    private static string Relative(string root, string path) => System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string FailureSummary(ProcessResult result)
    {
        var errors = (result.StdErr + Environment.NewLine + result.StdOut)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains(": error ", StringComparison.OrdinalIgnoreCase))
            .TakeLast(3)
            .Select(line => line.Trim())
            .ToArray();
        return errors.Length > 0 ? string.Join(" | ", errors) : result.LastLine;
    }

    [GeneratedRegex("^(?<path>.+)\\((?<line>\\d+),(?<column>\\d+)\\): warning CA1502: '(?<symbol>[^']+)' has a cyclomatic complexity of '(?<complexity>\\d+)'", RegexOptions.CultureInvariant)]
    private static partial Regex ComplexityDiagnostic();
}
