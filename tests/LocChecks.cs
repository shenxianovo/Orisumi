#:property PublishAot=false
using System.Diagnostics;
using System.Text.Json;

if (args.Length != 1)
    throw new ArgumentException("Provide the built orisumi-dev.dll path.");
var cli = Path.GetFullPath(args[0]);
var temp = Directory.CreateTempSubdirectory("orisumi-loc-");
try
{
    var root = temp.FullName;
    void Write(string path, string content)
    {
        var full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
    Write("Orisumi.slnx", "<Solution />");
    Write("src/Orisumi.Cli/Orisumi.Cli.csproj", "<Project />");
    Write(".gitignore", "ignored/\n");
    Write("src/Orisumi.Cli/main.cs", "// comment\n\nConsole.WriteLine(1); // trailing comment\n");
    Write("src/Orisumi.Cli/deleted.cs", "Console.WriteLine(0);\n");
    await Run("git", root, ["init", "-q"]);
    await Run("git", root, ["add", "."]);
    File.Delete(Path.Combine(root, "src/Orisumi.Cli/deleted.cs"));
    Write("src/Orisumi.Cli/main.cs", "// changed tracked file\nConsole.WriteLine(1);\nConsole.WriteLine(2);\n");
    Write("src/Orisumi.Dev/Orisumi.Dev.csproj", "<Project />");
    Write("src/Orisumi.Dev/dev.cs", "Console.WriteLine(3);\n");
    Write("src/Orisumi.Cli/Extension/package.json", "{\"name\":\"extension\"}");
    Write("src/Orisumi.Cli/Extension/main.ts", "/* comment */\nconst url = 'https://example.com/*literal*/';\n");
    Write("src/Orisumi.Cli/Extension/test/main.test.mjs", "// test comment\ncheck();\n");
    Write("src/Orisumi.Cli/Extension/package-lock.json", "{\n}\n");
    Write("tests/Unit/Unit.csproj", "<Project><ItemGroup><ProjectReference Include=\"../../src/Orisumi.Cli/Orisumi.Cli.csproj\" /></ItemGroup></Project>");
    Write("tests/Unit/Check.cs", "check();\n");
    Write("tests/loose.cs", "check();\n");
    Write("scripts/start.py", "# comment\nrun()\n");
    Write("notes.md", "# Notes\n\ntext\n");
    Write("loose [file].ts", "const stray = 1;\n");
    Write("ignored/hidden.cs", "hidden();\n");
    Write("ignored/package.json", "{\"name\":\"hidden\"}");
    Write("photo.png", "not source");
    Directory.CreateDirectory(Path.Combine(root, "nested", "working directory"));
    var nested = Path.Combine(root, "nested", "working directory");
    var first = await Run("dotnet", nested, [cli, "dev", "loc", "--json"]);
    using (var json = JsonDocument.Parse(first))
    {
        var report = json.RootElement;
        Check(report.GetProperty("code").GetProperty("implementation").GetInt32() == 5, "implementation totals");
        Check(report.GetProperty("code").GetProperty("tests").GetInt32() == 3, "test totals");
        var modules = report.GetProperty("modules").EnumerateArray().ToDictionary(row => row.GetProperty("name").GetString()!);
        Check(modules["Orisumi.Cli"].GetProperty("implementation").GetInt32() == 2 && modules["Orisumi.Dev"].GetProperty("implementation").GetInt32() == 1, "tracked changes and Dev classification");
        Check(modules["Orisumi.Cli"].GetProperty("tests").GetInt32() == 1, "test project reference");
        Check(modules["extension"].GetProperty("tests").GetInt32() == 1, "nearest nested project owns its tests");
        Check(modules["Cross-module"].GetProperty("tests").GetInt32() == 1, "standalone tests");
        Check(modules["Scripts"].GetProperty("implementation").GetInt32() == 1, "standalone scripts");
        Check(report.GetProperty("unclassifiedPaths").EnumerateArray().Select(path => path.GetString()).SequenceEqual(["loose [file].ts"]), "unclassified paths or ignored files");
        var roles = report.GetProperty("roles").EnumerateArray().ToDictionary(row => row.GetProperty("role").GetString()!);
        Check(roles["Documentation"].GetProperty("lines").GetInt32() == 2, "documentation outside code");
        Check(roles["Generated"].GetProperty("lines").GetInt32() == 2, "lockfiles outside code");
    }
    Console.WriteLine("PASS: worktree changes, untracked/ignored/deleted files, nested projects, roles and test ownership");

    var human = await Run("dotnet", nested, [cli, "dev", "loc"]);
    Check(human.StartsWith("Effective LOC · worktree · blank and comment-only lines excluded"), "legacy heading");
    Check(human.Contains("Outside code") && human.Contains("Module") && human.Contains("Language") && human.Contains("Impl") && human.Contains("Tests"), "legacy table sections");
    Check(human.Contains("█") && human.Contains("░") && human.Contains("loose [file].ts"), "bars or literal path escaping");
    Check(!human.Contains('\u001b') && !human.Split('\n').Any(line => line.EndsWith(' ')), "redirected output has control codes or trailing spaces");
    Console.WriteLine("PASS: legacy terminal layout, implementation/test bars and plain redirected output");

    Write("src/Orisumi.Cli/strings.cs", "var text = \"\"\"\n// string content\n/* string content */\n\"\"\";\n");
    Write("src/Orisumi.Cli/strings.swift", "let text = \"\"\"\n// string content\n/* string content */\n\"\"\"\n");
    Write("src/Orisumi.Cli/Extension/strings.ts", "const text = `\n// string content\n/* string content */\n`;\n");
    using (var json = JsonDocument.Parse(await Run("dotnet", nested, [cli, "dev", "loc", "--json"])))
        Check(json.RootElement.GetProperty("code").GetProperty("implementation").GetInt32() == 17, "comment markers inside multiline strings must count as code");
    Console.WriteLine("PASS: C#, Swift and TypeScript multiline strings");

    var empty = Path.Combine(root, "empty");
    Directory.CreateDirectory(empty);
    await Run("git", empty, ["init", "-q"]);
    Write("empty/Orisumi.slnx", "<Solution />");
    Write("empty/src/Orisumi.Dev/Orisumi.Dev.csproj", "<Project />");
    var emptyText = await Run("dotnet", empty, [cli, "dev", "loc"]);
    Check(emptyText.Contains("Effective LOC") && emptyText.Contains("Total"), "empty code report");
    Console.WriteLine("PASS: checkout without code files");
}
finally
{
    temp.Delete(recursive: true);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static async Task<string> Run(string executable, string directory, string[] arguments)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var result = await Process.RunAndCaptureTextAsync(new ProcessStartInfo(executable, arguments)
    {
        WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true,
    }, timeout.Token);
    Check(result.ExitStatus is { ExitCode: 0, Canceled: false }, result.StandardError);
    return result.StandardOutput;
}
