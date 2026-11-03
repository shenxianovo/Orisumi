using System.Text.Json;
using Orisumi.Dev;

namespace Orisumi.Dev.Tests;

public sealed class ConfigTests
{
    private const string Key = "sk-test-0123456789abcdef";

    [Test]
    public async Task SetKeepsTheSecretForTheCurrentUserAndListMasksIt()
    {
        using var fixture = new Fixture();
        await Assert.That(await fixture.RunAsync("config", "set", "openai.api-key", Key)).IsEqualTo(0);
        if (!OperatingSystem.IsWindows())
            await Assert.That(File.GetUnixFileMode(fixture.Config.Path)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await Assert.That(fixture.Config.Get(UserConfig.OpenAIApiKey)).IsEqualTo(Key);

        fixture.Output.GetStringBuilder().Clear();
        await Assert.That(await fixture.RunAsync("config", "list", "--json")).IsEqualTo(0);
        var listed = fixture.Output.ToString();
        await Assert.That(listed).DoesNotContain(Key);
        using var result = JsonDocument.Parse(listed);
        await Assert.That(result.RootElement.GetProperty("settings")[0].GetProperty("value").GetString()).IsEqualTo("sk-t…cdef");
    }

    [Test]
    public async Task SetWithoutAValueReadsRedirectedInput()
    {
        using var fixture = new Fixture(input: Key + "\n");
        await Assert.That(await fixture.RunAsync("config", "set", "openai.api-key")).IsEqualTo(0);
        await Assert.That(fixture.Config.Get(UserConfig.OpenAIApiKey)).IsEqualTo(Key);
    }

    [Test]
    public async Task UnsetRemovesTheValue()
    {
        using var fixture = new Fixture();
        fixture.Config.Set(UserConfig.OpenAIApiKey, Key);
        await Assert.That(await fixture.RunAsync("config", "unset", "openai.api-key")).IsEqualTo(0);
        await Assert.That(fixture.Config.Get(UserConfig.OpenAIApiKey)).IsNull();
    }

    [Test]
    [Arguments("config set openai.key value")]
    [Arguments("config unset openai.key")]
    [Arguments("review check docs/a.md --all")]
    [Arguments("review calibrate no-such-rule")]
    public async Task InvalidRequestsAreUsageErrors(string arguments)
    {
        using var fixture = new Fixture();
        await Assert.That(await fixture.RunAsync(arguments.Split(' '))).IsEqualTo(2);
        await Assert.That(File.Exists(fixture.Config.Path)).IsFalse();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "orisumi-config-tests-" + Guid.NewGuid().ToString("N"));
        private readonly string _input;

        public Fixture(string input = "")
        {
            _input = input;
            Directory.CreateDirectory(_root);
            Config = new UserConfig(Path.Combine(_root, "home", "config.json"));
        }

        public UserConfig Config { get; }
        public StringWriter Output { get; } = new();

        public Task<int> RunAsync(params string[] arguments) =>
            new DeveloperCli(new RepositoryContext(_root), new NoProcesses(), Output, TextWriter.Null,
                new ModelAccess(Config, _ => throw new InvalidOperationException("no model calls")), new StringReader(_input))
                .RunAsync(arguments, CancellationToken.None);

        public void Dispose()
        {
            Output.Dispose();
            Directory.Delete(_root, true);
        }
    }

    private sealed class NoProcesses : IProcessRunner
    {
        public Task<ProcessResult> CaptureAsync(ProcessCommand command, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no processes");
    }
}
