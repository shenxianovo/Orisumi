#:property PublishAot=false
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

if (args.Length != 2)
    throw new ArgumentException("Provide the installed orisumi-dev launcher and the host output directory.");
var tool = Path.GetFullPath(args[0]);
var hostBuild = Path.GetFullPath(args[1]);
var temp = Directory.CreateTempSubdirectory("orisumi-launcher-check-");
try
{
    var root = Path.Combine(temp.FullName, "checkout with spaces");
    var project = Path.Combine(root, "Host", "Cli");
    var dev = Path.Combine(root, "Tools", "Dev");
    Directory.CreateDirectory(dev);
    var nested = Path.Combine(root, "nested", "working directory");
    Directory.CreateDirectory(project);
    Directory.CreateDirectory(nested);
    await File.WriteAllTextAsync(Path.Combine(root, "Orisumi.slnx"), """
        <Solution>
          <Project Path="Tools/Dev/Orisumi.Dev.csproj" />
          <Project Path="Collectors/Fixture/Orisumi.Collectors.Fixture.csproj" />
        </Solution>
        """);
    var collector = Directory.CreateDirectory(Path.Combine(root, "Collectors/Fixture")).FullName;
    await File.WriteAllTextAsync(Path.Combine(collector, "Orisumi.Collectors.Fixture.csproj"), """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net11.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <AssemblyName>orisumi-fixture</AssemblyName>
          </PropertyGroup>
        </Project>
        """);
    var collectorSource = Path.Combine(collector, "Program.cs");
    const string fixture = """"
        using System.Text.Json;
        using System.Runtime.InteropServices;
        if (args is ["describe"])
        {
            Console.WriteLine("""{"name":"forwarded","version":"0.1.0+fixture","arguments":["capture"]}""");
            return 0;
        }
        if (args.Length < 3 || args[0] != "--store") return 2;
        switch (args[2])
        {
            case "binary":
                await Console.OpenStandardInput().CopyToAsync(Console.OpenStandardOutput());
                Console.Error.WriteLine("fixture stderr");
                return 23;
            case "wait":
                var stopped = new TaskCompletionSource();
                using (PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal =>
                {
                    signal.Cancel = true;
                    stopped.TrySetResult();
                }))
                {
                    Console.WriteLine("READY");
                    await stopped.Task;
                    Console.WriteLine("STOPPED");
                }
                return 37;
            default:
                Console.Write(JsonSerializer.Serialize(args));
                return 0;
        }
        """";
    await File.WriteAllTextAsync(collectorSource, fixture);
    await File.WriteAllTextAsync(Path.Combine(root, ".git"), "gitdir: /nonexistent-test-worktree-metadata");
    await File.WriteAllTextAsync(Path.Combine(dev, "Orisumi.Dev.csproj"), """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net11.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
          </PropertyGroup>
        </Project>
        """);
    const string program = """
        #warning fixture build diagnostic
        using System.Text.Json;
        using System.Runtime.InteropServices;
        if (args is ["binary"])
        {
            await Console.OpenStandardInput().CopyToAsync(Console.OpenStandardOutput());
            return 0;
        }
        if (args is ["wait"])
        {
            var stopped = new TaskCompletionSource();
            using var termination = PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal =>
            {
                signal.Cancel = true;
                stopped.TrySetResult();
            });
            Console.WriteLine("READY");
            await stopped.Task;
            File.WriteAllText("stopped.txt", "flushed");
            Console.WriteLine("STOPPED");
            return 0;
        }
        Console.Write(JsonSerializer.Serialize(new { version = "VERSION", args, cwd = Environment.CurrentDirectory }));
        return args is ["exit"] ? 23 : 0;
        """;
    var source = Path.Combine(dev, "Program.cs");
    await File.WriteAllTextAsync(source, program.Replace("VERSION", "v1"));
    string[] arguments = ["--unknown-option", "包含 空格", "quote\"value", "[literal]", "line\nbreak"];
    var first = await Run(tool, nested, arguments);
    Check(first.Code == 0, first.Error);
    using (var result = JsonDocument.Parse(first.Output))
    {
        Check(result.RootElement.GetProperty("version").GetString() == "v1", "first source version");
        Check(result.RootElement.GetProperty("args").EnumerateArray().Select(x => x.GetString()).SequenceEqual(arguments), "arguments changed");
        Check(result.RootElement.GetProperty("cwd").GetString()!.EndsWith(Path.Combine("nested", "working directory"), StringComparison.Ordinal), "working directory changed");
    }
    Check(first.Error.Contains("fixture build diagnostic"), "build diagnostic did not go to stderr");
    Check(!Directory.Exists(Path.Combine(collector, "bin")), "source launcher builds only the development project");
    Console.WriteLine("PASS: nested checkout, spaces, Git directory-file marker, args, cwd, stderr");

    await File.WriteAllTextAsync(source, program.Replace("VERSION", "v2"));
    await File.AppendAllTextAsync(collectorSource, "\ninvalid C# source");
    var second = await Run(tool, nested, []);
    Check(second.Code == 0 && second.Output.Contains("v2"), second.Error);
    await File.AppendAllTextAsync(source, "\ninvalid C# source");
    var broken = await Run(tool, nested, []);
    Check(broken.Code != 0 && broken.Output.Length == 0 && broken.Error.Contains("CS"), $"build failure: code={broken.Code}, stdout={broken.Output}, stderr={broken.Error}");
    await File.WriteAllTextAsync(collectorSource, fixture);
    await File.WriteAllTextAsync(source, program.Replace("VERSION", "v3"));
    var recovered = await Run(tool, nested, ["exit"]);
    Check(recovered.Code == 23 && recovered.Output.Contains("v3"), "child exit code or recovered source lost");
    Console.WriteLine("PASS: uncommitted updates, build failure stops stale execution, recovery and exit codes");

    var binary = new byte[] {0, 255, 128, 10, 13, 0, 42};
    using (var process = Start(tool, nested, ["binary"]))
    {
        var errors = process.StandardError.ReadToEndAsync();
        using var output = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        await process.StandardInput.BaseStream.WriteAsync(binary);
        process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        await copy;
        Check(process.ExitCode == 0 && output.ToArray().SequenceEqual(binary), await errors);
    }
    Console.WriteLine("PASS: binary stdin/stdout stays intact through build and delegation");

    var outside = await Run(tool, temp.FullName, ["--help"]);
    Check(outside.Code != 0 && outside.Error.Contains("Orisumi 仓库"), "outside checkout did not stop");
    var unrelated = Path.Combine(root, "unrelated");
    Directory.CreateDirectory(Path.Combine(unrelated, ".git"));
    var other = await Run(tool, unrelated, ["--help"]);
    Check(other.Code != 0 && other.Error.Contains("Orisumi 仓库"), "escaped nested unrelated repository");
    Console.WriteLine("PASS: outside checkout and nested unrelated repository are rejected");

    if (!OperatingSystem.IsWindows())
    using (var process = Start(tool, nested, ["wait"]))
    {
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            Check(await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60)) == "READY", "child never started");
            Check(process.Signal(PosixSignal.SIGTERM), "SIGTERM was not delivered");
            var remaining = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Check(process.ExitCode == 130 && (await remaining).Contains("STOPPED") &&
                await File.ReadAllTextAsync(Path.Combine(nested, "stopped.txt")) == "flushed", await errors);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
    if (!OperatingSystem.IsWindows())
        Console.WriteLine("PASS: termination reaches source process and waits for it to flush");

    var collectorBuild = await Run("dotnet", root, ["build", Path.Combine(collector, "Orisumi.Collectors.Fixture.csproj")]);
    Check(collectorBuild.Code == 0, collectorBuild.Output + collectorBuild.Error);
    var relativeOutput = Path.Combine("fixture-output", "custom-config", "custom-framework");
    var hostOutput = Path.Combine(project, relativeOutput);
    CopyOutput(hostBuild, hostOutput);
    var collectorOutput = await Run("dotnet", collector, ["msbuild", "Orisumi.Collectors.Fixture.csproj",
        "-nologo", "-p:Configuration=Debug", "-getProperty:TargetDir"]);
    Check(collectorOutput.Code == 0, collectorOutput.Error);
    CopyOutput(collectorOutput.Output.Trim(), Path.Combine(collector, relativeOutput));
    var host = Path.Combine(hostOutput, "orisumi");
    var store = Path.Combine(temp.FullName, "custom store ' 引号");
    Directory.CreateDirectory(store);
    File.WriteAllText(Path.Combine(store, "instance.json"), """{"name":"test_launcher"}""");
    var forwarded = await Run(host, nested, ["--store", store, "forwarded", "args", .. arguments]);
    Check(forwarded.Code == 0, forwarded.Error);
    Check(JsonSerializer.Deserialize<string[]>(forwarded.Output)!.SequenceEqual(["--store", store, "args", .. arguments]),
        "store and collector arguments changed");
    var unknown = await Run(host, nested, ["--store", store, "not-a-collector"]);
    Check(unknown.Code != 0 && unknown.Error.Contains("not-a-collector"), "unknown collector should reach the host parser");
    Console.WriteLine("PASS: discovery follows the host's relative output and describe name, and forwards literal arguments and store");

    using (var process = Start(host, nested, ["--store", store, "forwarded", "binary"]))
    {
        var errors = process.StandardError.ReadToEndAsync();
        using var output = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(binary);
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await copy;
            Check(process.ExitCode == 23 && output.ToArray().SequenceEqual(binary) && (await errors).Trim() == "fixture stderr",
                "host changed binary stdin/stdout, stderr or the collector exit code");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }
    Console.WriteLine("PASS: host preserves collector stdin, stdout, stderr and exit code");

    using (var process = Start(host, nested, ["--store", store, "forwarded", "wait"]))
    {
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            Check(await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)) == "READY",
                "collector was not ready");
            Check(process.Signal(PosixSignal.SIGTERM), "SIGTERM was not delivered");
            var remaining = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Check(process.ExitCode == 37 && (await remaining).Trim() == "STOPPED", await errors);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }
    Console.WriteLine("PASS: host relays SIGTERM and returns the collector's shutdown status");

}
finally
{
    temp.Delete(recursive: true);
}

static void CopyOutput(string from, string to)
{
    Directory.CreateDirectory(to);
    foreach (var file in Directory.EnumerateFiles(from))
        File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
    foreach (var directory in Directory.EnumerateDirectories(from))
        CopyOutput(directory, Path.Combine(to, Path.GetFileName(directory)));
}

static void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
}

static Process Start(string executable, string cwd, string[] arguments)
{
    var start = new ProcessStartInfo(executable)
    {
        WorkingDirectory = cwd,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = true,
    };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    return Process.Start(start)!;
}

static async Task<(int Code, string Output, string Error)> Run(string executable, string cwd, string[] arguments)
{
    using var process = Start(executable, cwd, arguments);
    process.StandardInput.Close();
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    try
    {
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        return (process.ExitCode, await output, await error);
    }
    finally
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
    }
}
