using System.Diagnostics;
using Orisumi.Platform;

// Copy the SDK apphost with its managed payload so discovery and describe cross a real process boundary.
internal sealed class FixtureProgram(string build)
{
    internal static string FileName(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    internal static async Task<FixtureProgram> Build(string directory)
    {
        var result = await Process.RunAndCaptureTextAsync("dotnet",
            ["build", "Tests/Fixtures/FixtureTool.cs", "-o", directory, "-p:TreatWarningsAsErrors=true"], default);
        if (result.ExitStatus.ExitCode != 0) throw new IOException(result.StandardOutput + result.StandardError);
        return new(directory);
    }

    internal string Tool(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.EnumerateFiles(build, "FixtureTool.*"))
            if (Path.GetFileName(file) != FileName("FixtureTool"))
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), overwrite: true);
        var path = Path.Combine(directory, FileName(name));
        File.Copy(Path.Combine(build, FileName("FixtureTool")), path);
        return path;
    }

    internal void Describe(string executable, string name, string version, string[]? arguments)
    {
        var directory = Path.GetDirectoryName(executable)!;
        Tool(directory, Path.GetFileNameWithoutExtension(executable));
        using var output = File.CreateText(Path.Combine(directory, "describe.json"));
        new ProgramDescription(name, version, arguments).Write(output);
    }
}
