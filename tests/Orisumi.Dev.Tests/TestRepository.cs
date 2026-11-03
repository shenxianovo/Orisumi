using Orisumi.Dev;

namespace Orisumi.Dev.Tests;

internal sealed class TestRepository : IDisposable
{
    public const string Solution = """
        <Solution>
          <Folder Name="/tests/">
            <Project Path="tests/Example.Tests/Example.Tests.csproj" />
          </Folder>
          <Folder Name="/tools/">
            <Project Path="tools/Example/Example.csproj" />
          </Folder>
        </Solution>
        """;

    public RepositoryContext Context { get; } = new(Path.Combine(Path.GetTempPath(), "orisumi-tests-" + Guid.NewGuid().ToString("N")));

    public StringWriter Output { get; } = new();

    public TestRepository(bool withProjects = true)
    {
        Directory.CreateDirectory(Context.Root);
        Git("init", "-q", "-b", "main");
        Write(".gitignore", ".artifacts/\n");
        if (withProjects)
        {
            Write("Orisumi.slnx", Solution);
            Write("tools/Example/Example.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            Write("tools/Example/Example.cs", "class Example { }");
            Write("tests/Example.Tests/Example.Tests.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"../../tools/Example/Example.csproj\" /></ItemGroup></Project>");
        }
        Commit();
    }

    public void Write(string path, string text)
    {
        var full = Context.Path(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    public void Commit()
    {
        Git("add", "-A");
        Git("-c", "user.name=test", "-c", "user.email=test@example.com", "commit", "-q", "--allow-empty", "-m", "test");
    }

    private void Git(params string[] arguments)
    {
        var result = new ProcessRunner(Context.Root).CaptureAsync(new("git", arguments), CancellationToken.None).GetAwaiter().GetResult();
        if (result.ExitCode != 0) throw new InvalidOperationException(result.StdErr);
    }

    public void Dispose()
    {
        Output.Dispose();
        Directory.Delete(Context.Root, recursive: true);
    }
}

internal sealed class FixedRunner(int exitCode) : IProcessRunner
{
    public bool Cancel { get; init; }

    public List<ProcessCommand> Calls { get; } = [];

    public Task<ProcessResult> CaptureAsync(ProcessCommand command, CancellationToken cancellationToken)
    {
        Calls.Add(command);
        if (Cancel) throw new CapturedProcessCancelledException("partial output", "", cancellationToken);
        return Task.FromResult(new ProcessResult(exitCode, "build or test output", ""));
    }
}
