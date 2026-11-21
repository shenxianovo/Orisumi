using System.CommandLine;
using System.ComponentModel;
using System.Diagnostics;
using Orisumi.Platform;

namespace Orisumi.Dev;

internal static class CheckCommand
{
    internal static Command Create()
    {
        var check = new Command("check", "运行全部开发验证。");
        check.SetAction((_, token) => Run(Repository.Require(), Console.Out, token));
        return check;
    }

    private static async Task<int> Run(string checkout, TextWriter output, CancellationToken token)
    {
        var temporary = Directory.CreateTempSubdirectory("orisumi-check-");
        try
        {
            var packages = Path.Combine(temporary.FullName, "packages");
            var tools = Path.Combine(temporary.FullName, "tools");
            (string Name, (string Executable, string[] Arguments)[] Steps)[] checks =
            [
                ("Build", [Dotnet("build", "Orisumi.slnx")]),
                FileCheck("Platform"),
                FileCheck("CaptureStartup"),
                FileCheck("CaptureStatus"),
                FileCheck("Schema"),
                FileCheck("CaptureIntegration"),
                FileCheck("BrowserIntegration"),
                FileCheck("CaptureCoverage"),
                FileCheck("Export"),
                FileCheck("ClaudeIncremental"),
                FileCheck("CodexIncremental"),
                FileCheck("MacOSObservation"),
                FileCheck("Instance"),
                ("Browser extension", [("npm", ["--prefix", DevCommand.ExtensionDirectory, "ci"]),
                    ("npm", ["--prefix", DevCommand.ExtensionDirectory, "test"])]),
                ("App.MacOS", [("swift", ["test", "--package-path", ReleaseBuild.AppPackage])]),
                ("SourceLauncher", [Dotnet("pack", Repository.DevProject, "-c", "Release", "-o", packages),
                    Dotnet("tool", "install", "Orisumi.Dev", "--tool-path", tools, "--source", packages),
                    RunFile("SourceLauncher", Path.Combine(tools, "orisumi-dev"), "Host/Cli/bin/Debug/net11.0")]),
                FileCheck("Loc", "Tools/Dev/bin/Debug/net11.0/orisumi-dev.dll"),
            ];

            var failed = false;
            foreach (var (name, steps) in checks)
            {
                var elapsed = Stopwatch.StartNew();
                using var log = new StringWriter();
                var capture = TextWriter.Synchronized(log);
                var passed = true;
                foreach (var (executable, arguments) in steps)
                {
                    try
                    {
                        if (await RunProcess.Run(executable, arguments, capture, token, directory: checkout, errors: capture) == 0)
                            continue;
                    }
                    catch (Win32Exception error) { capture.WriteLine(error.Message); }
                    token.ThrowIfCancellationRequested();
                    passed = false;
                    break;
                }
                output.WriteLine(FormattableString.Invariant($"{(passed ? "PASS" : "FAIL")} {name} ({elapsed.Elapsed.TotalSeconds:F2}s)"));
                if (passed) continue;
                failed = true;
                output.WriteLine(log.ToString().TrimEnd());
            }
            return failed ? 1 : 0;
        }
        finally { temporary.Delete(recursive: true); }
    }

    private static (string, string[]) Dotnet(params string[] arguments) => (RunProcess.Dotnet, arguments);

    private static (string Name, (string Executable, string[] Arguments)[] Steps) FileCheck(string name, params string[] arguments) =>
        (name, [RunFile(name, arguments)]);

    private static (string, string[]) RunFile(string name, params string[] arguments) =>
        Dotnet(arguments.Length == 0 ? ["run", "--file", $"Tests/{name}Checks.cs"]
            : ["run", "--file", $"Tests/{name}Checks.cs", "--", .. arguments]);
}
