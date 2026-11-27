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
            var dev = await TargetPath(Repository.DevProject);
            var host = await TargetPath(Repository.CliProject);
            var packages = Path.Combine(temporary.FullName, "packages");
            var tools = Path.Combine(temporary.FullName, "tools");
            var launcher = Path.Combine(tools, OperatingSystem.IsWindows() ? "orisumi-dev.exe" : "orisumi-dev");
            const string windows = "win-x64";
            (string Name, (string Executable, string[] Arguments)[] Steps)[] checks =
            [
                ("Build", [.. DevPlatform.BuildTargets(checkout).Select(project => Dotnet("build", project, "-warnaserror"))]),
                ("Windows compile", [.. Repository.ManagedProjects(checkout, windows).Select(project =>
                    Dotnet("build", project, "-c", "Release", "-r", windows, "-p:PublishAot=false", "-warnaserror", "-tl:off"))]),
                FileCheck("Platform"),
                FileCheck("CaptureStartup"),
                FileCheck("CaptureStatus"),
                FileCheck("Schema"),
                FileCheck("CaptureIntegration"),
                FileCheck("BrowserIntegration"),
                FileCheck("CaptureCoverage"),
                FileCheck("Day"),
                FileCheck("Export"),
                FileCheck("ClaudeIncremental"),
                FileCheck("CodexIncremental"),
                FileCheck("Instance"),
                .. DevPlatform.NativeChecks(),
                ("SourceLauncher", [Dotnet("pack", Repository.DevProject, "-c", "Release", "-o", packages),
                    Dotnet("tool", "install", "Orisumi.Dev", "--tool-path", tools, "--source", packages),
                    RunFile("SourceLauncher", launcher, Path.GetDirectoryName(host)!)]),
                ("Browser extension", [("npm", ["--prefix", DevCommand.ExtensionDirectory, "ci"]),
                    ("npm", ["--prefix", DevCommand.ExtensionDirectory, "test"])]),
                FileCheck("Loc", dev),
            ];

            async Task<string> TargetPath(string project) => (await Repository.Query(RunProcess.Dotnet, checkout, token,
                ["msbuild", project, "-nologo", "-p:Configuration=Debug", "-getProperty:TargetPath"])).Trim();

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
                        var command = Development.Command(executable, arguments);
                        if (await RunProcess.Run(command.Executable, command.Arguments, capture, token, directory: checkout, errors: capture) == 0)
                            continue;
                    }
                    catch (Exception error) when (error is Win32Exception or FileNotFoundException) { capture.WriteLine(error.Message); }
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

    internal static (string, string[]) Dotnet(params string[] arguments) => (RunProcess.Dotnet, arguments);

    internal static (string Name, (string Executable, string[] Arguments)[] Steps) FileCheck(string name, params string[] arguments) =>
        (name, [RunFile(name, arguments)]);

    internal static (string, string[]) RunFile(string name, params string[] arguments) =>
        Dotnet(arguments.Length == 0 ? ["run", "--file", $"Tests/{name}Checks.cs"]
            : ["run", "--file", $"Tests/{name}Checks.cs", "--", .. arguments]);
}
