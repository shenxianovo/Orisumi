using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Orisumi.Raw;

namespace Orisumi.Capturing.Tests;

public class GitCollectorTests
{
    [Test]
    public async Task RewrittenReferencesLeaveBothCapturesIndependentlyRecoverable()
    {
        using var sample = new GitSample();
        sample.Write("旧名.txt", "first\n");
        sample.Write("目录/中文.txt", "你好\n");
        byte[] binary = [0, 255, 128, 10];
        File.WriteAllBytes(Path.Combine(sample.Repository, "binary.dat"), binary);
        var first = sample.Commit("first");
        sample.Git("tag", "-a", "v1", "-m", "first version");
        sample.Git("mv", "旧名.txt", "新名.txt");
        sample.Write("新名.txt", "second\n");
        var second = sample.Commit("second");
        sample.Git("checkout", "--detach");
        sample.Write("detached.txt", "detached\n");
        var detached = sample.Commit("detached");
        var refsA = sample.Git("show-ref", "--dereference");
        var captureA = (await sample.Collector.ScanAsync(sample.Repository)).Result!.Capture;

        sample.Git("checkout", "main");
        sample.Git("reset", "--hard", first);
        sample.Git("tag", "-d", "v1");
        sample.Write("旧名.txt", "rewritten\n");
        var rewritten = sample.Commit("rewritten");
        sample.Git("checkout", "-b", "a-current");
        var refsB = sample.Git("show-ref", "--dereference");
        var captureB = (await sample.Collector.ScanAsync(sample.Repository + Path.DirectorySeparatorChar)).Result!.Capture;
        Directory.Move(sample.Repository, sample.Repository + "-isolated");

        await Assert.That(captureA.Record.Failure).IsNull();
        await Assert.That(captureB.Record.Failure).IsNull();
        await Assert.That(captureA.Source).IsEqualTo("git");
        await Assert.That(captureB.Record.Origin).IsEqualTo(sample.Repository);
        var restoredA = sample.Restore(captureA, "A");
        var restoredB = sample.Restore(captureB, "B");
        await Assert.That(GitSample.GitAt(restoredA, "show-ref", "--dereference")).IsEqualTo(refsA);
        await Assert.That(GitSample.GitAt(restoredB, "show-ref", "--dereference")).IsEqualTo(refsB);
        await Assert.That(GitSample.GitAt(restoredA, "show", first + ":旧名.txt")).IsEqualTo("first");
        await Assert.That(GitSample.GitAt(restoredA, "show", second + ":新名.txt")).IsEqualTo("second");
        await Assert.That(GitSample.GitAt(restoredA, "show", detached + ":detached.txt")).IsEqualTo("detached");
        await Assert.That(GitSample.GitAt(restoredB, "show", rewritten + ":旧名.txt")).IsEqualTo("rewritten");
        await Assert.That(GitSample.GitAt(restoredA, "show", first + ":目录/中文.txt")).IsEqualTo("你好");
        await Assert.That(GitSample.Run(restoredA, "show", first + ":binary.dat").SequenceEqual(binary)).IsTrue();
        await Assert.That(sample.Read(captureA, "commands/head-before-object/stdout").Trim()).IsEqualTo(detached);
        await Assert.That(sample.Read(captureB, "commands/head-before-symbolic/stdout")).IsEqualTo("refs/heads/a-current\n");
        GitSample.GitAt(restoredA, "update-ref", "--no-deref", "HEAD", sample.Read(captureA, "commands/head-before-object/stdout").Trim());
        GitSample.GitAt(restoredB, "symbolic-ref", "HEAD", sample.Read(captureB, "commands/head-before-symbolic/stdout").Trim());
        await Assert.That(GitSample.GitAt(restoredA, "rev-parse", "HEAD")).IsEqualTo(detached);
        await Assert.That(GitSample.GitAt(restoredB, "symbolic-ref", "HEAD")).IsEqualTo("refs/heads/a-current");
        using var command = JsonDocument.Parse(sample.Read(captureA, "commands/bundle/command.json"));
        await Assert.That(command.RootElement.GetProperty("argv").EnumerateArray().Select(value => value.GetString())
            .SequenceEqual(["-c", "pack.threads=1", "bundle", "create", "-", "--all"])).IsTrue();
        await Assert.That(command.RootElement.GetProperty("stdout").GetString()).IsEqualTo("repository.bundle");
        await Assert.That(command.RootElement.GetProperty("cwd").GetString()).IsEqualTo(sample.Repository);
        await Assert.That(command.RootElement.GetProperty("exitCode").GetInt32()).IsEqualTo(0);
        await Assert.That(command.RootElement.GetProperty("environment").GetProperty("GIT_NO_LAZY_FETCH").GetString()).IsEqualTo("1");
        await Assert.That(command.RootElement.GetProperty("completedAt").GetDateTimeOffset()
            >= command.RootElement.GetProperty("startedAt").GetDateTimeOffset()).IsTrue();
    }

    [Test]
    public async Task EmptyRepositoryKeepsItsObservedStateAndFailedBundleAttempt()
    {
        using var sample = new GitSample();
        var round = await sample.Collector.ScanAsync(sample.Repository);
        var capture = round.Result!.Capture;
        await Assert.That(round.Failure).IsNotNull();
        await Assert.That(capture.Record.Manifest).IsNotNull();
        await Assert.That(sample.Read(capture, "commands/refs-before/stdout")).IsEqualTo("");
        await Assert.That(sample.Read(capture, "commands/head-before-symbolic/stdout")).IsEqualTo("refs/heads/main\n");
        await Assert.That(sample.Read(capture, "commands/bundle/stderr").Length > 0).IsTrue();
        using var command = JsonDocument.Parse(sample.Read(capture, "commands/bundle/command.json"));
        await Assert.That(command.RootElement.GetProperty("exitCode").GetInt32()).IsEqualTo(128);
        await Assert.That(sample.Store.Manifest(capture).Any(entry => entry.Path == "commands/bundle-verify/command.json")).IsFalse();
    }

    [Test]
    public async Task MissingReachableBlobKeepsTheGitErrorInsteadOfPublishingACompleteCapture()
    {
        using var sample = new GitSample();
        sample.Write("body.txt", "needed body\n");
        sample.Commit("first");
        var blob = sample.Git("rev-parse", "HEAD:body.txt");
        File.Delete(Path.Combine(sample.Repository, ".git", "objects", blob[..2], blob[2..]));
        var round = await sample.Collector.ScanAsync(sample.Repository);
        await Assert.That(round.Failure).IsNotNull();
        using var command = JsonDocument.Parse(sample.Read(round.Result!.Capture, "commands/bundle/command.json"));
        await Assert.That(command.RootElement.GetProperty("exitCode").GetInt32() != 0).IsTrue();
        await Assert.That(sample.Read(round.Result.Capture, "commands/bundle/stderr").Length > 0).IsTrue();
        await Assert.That(sample.Store.Manifest(round.Result.Capture).Any(entry => entry.Path == "repository.bundle")).IsTrue();
    }

    [Test]
    public async Task MissingPromisorBlobIsNotFetchedBackIntoTheSourceRepository()
    {
        using var sample = new GitSample();
        sample.Write("body.txt", "needed body\n");
        sample.Commit("first");
        var remote = Path.Combine(sample.DirectoryPath, "remote.git");
        GitSample.GitAt(sample.DirectoryPath, "clone", "--bare", "--template=", sample.Repository, remote);
        sample.Git("config", "--local", "extensions.partialClone", "origin");
        sample.Git("config", "--local", "remote.origin.promisor", "true");
        sample.Git("config", "--local", "remote.origin.url", remote);
        var blob = sample.Git("rev-parse", "HEAD:body.txt");
        var objectPath = Path.Combine(sample.Repository, ".git", "objects", blob[..2], blob[2..]);
        File.Delete(objectPath);
        var configuration = File.ReadAllBytes(Path.Combine(sample.Repository, ".git", "config"));
        var round = await sample.Collector.ScanAsync(sample.Repository);
        await Assert.That(round.Failure).IsNotNull();
        await Assert.That(File.Exists(objectPath)).IsFalse();
        await Assert.That(File.ReadAllBytes(Path.Combine(sample.Repository, ".git", "config")).SequenceEqual(configuration)).IsTrue();
        using var command = JsonDocument.Parse(sample.Read(round.Result!.Capture, "commands/bundle/command.json"));
        await Assert.That(command.RootElement.GetProperty("environment").GetProperty("GIT_ALLOW_PROTOCOL").GetString()).IsEqualTo("");
        await Assert.That(command.RootElement.GetProperty("exitCode").GetInt32() != 0).IsTrue();
    }

    [Test]
    public async Task ShallowHistoryIsRecordedWithItsObservedBoundary()
    {
        using var sample = new GitSample();
        sample.Write("body.txt", "first\n");
        sample.Commit("first");
        sample.Write("body.txt", "second\n");
        sample.Commit("second");
        var shallow = Path.Combine(sample.DirectoryPath, "shallow");
        GitSample.GitAt(sample.DirectoryPath, "clone", "--depth=1", "--template=", new Uri(sample.Repository).AbsoluteUri, shallow);
        var round = await sample.Collector.ScanAsync(shallow);
        await Assert.That(round.Failure!.Contains("shallow", StringComparison.Ordinal)).IsTrue();
        await Assert.That(sample.Read(round.Result!.Capture, "commands/shallow/stdout")).IsEqualTo("true\n");
    }

    [Test]
    public async Task FailedBundleProcessPreservesItsExactBinaryPrefixAndStatus()
    {
        if (OperatingSystem.IsWindows()) return;
        using var sample = new GitSample();
        sample.Write("body.txt", "body");
        sample.Commit("first");
        var executable = sample.BundleProgram("printf '\\377\\000unfinished'\nprintf 'pack failed\\n' >&2\nexit 17\n");
        var round = await new GitCollector(sample.StoreDirectory, "tests", executable).ScanAsync(sample.Repository);
        await Assert.That(round.Failure).IsNotNull();
        using var prefix = sample.Store.Open(round.Result!.Capture, "repository.bundle");
        using var bytes = new MemoryStream();
        prefix.CopyTo(bytes);
        await Assert.That(bytes.ToArray().SequenceEqual(new byte[] { 255, 0 }.Concat(Encoding.UTF8.GetBytes("unfinished")))).IsTrue();
        await Assert.That(sample.Read(round.Result.Capture, "commands/bundle/stderr")).IsEqualTo("pack failed\n");
        using var command = JsonDocument.Parse(sample.Read(round.Result.Capture, "commands/bundle/command.json"));
        await Assert.That(command.RootElement.GetProperty("exitCode").GetInt32()).IsEqualTo(17);
        await Assert.That(command.RootElement.GetProperty("cancelled").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task CancellationStopsTheProcessTreeAndKeepsTheOutputAlreadyReceived()
    {
        if (OperatingSystem.IsWindows()) return;
        using var sample = new GitSample();
        sample.Write("body.txt", "body");
        sample.Commit("first");
        var pidFile = Path.Combine(sample.DirectoryPath, "processes");
        var executable = sample.BundleProgram($$"""
            printf 'bundle-prefix'
            printf 'still writing\n' >&2
            sleep 60 &
            printf '%s\n%s\n' "$$" "$!" > '{{pidFile}}'
            wait
            """);
        using var stop = new CancellationTokenSource();
        var running = new GitCollector(sample.StoreDirectory, "tests", executable).ScanAsync(sample.Repository, stop.Token);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(pidFile)) await Task.Delay(10, deadline.Token);
            var pids = (await File.ReadAllLinesAsync(pidFile, deadline.Token)).Select(value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            using var parent = Process.GetProcessById(pids[0]);
            using var child = Process.GetProcessById(pids[1]);
            stop.Cancel();
            var round = await running.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(parent.HasExited).IsTrue();
            await Assert.That(child.HasExited).IsTrue();
            await Assert.That(round.Failure!.Contains("取消", StringComparison.Ordinal)).IsTrue();
            await Assert.That(sample.Read(round.Result!.Capture, "repository.bundle")).IsEqualTo("bundle-prefix");
            await Assert.That(sample.Read(round.Result.Capture, "commands/bundle/stderr")).IsEqualTo("still writing\n");
            using var command = JsonDocument.Parse(sample.Read(round.Result.Capture, "commands/bundle/command.json"));
            await Assert.That(command.RootElement.GetProperty("cancelled").GetBoolean()).IsTrue();
            await Assert.That(command.RootElement.GetProperty("exitCode").GetInt32() != 0).IsTrue();
        }
        finally
        {
            stop.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Test]
    public async Task MissingGitExecutableKeepsTheStartFailureAndAttemptedArguments()
    {
        using var sample = new GitSample();
        var round = await new GitCollector(sample.StoreDirectory, "tests", Path.Combine(sample.DirectoryPath, "missing-git"))
            .ScanAsync(sample.Repository);
        await Assert.That(round.Failure).IsNotNull();
        using var command = JsonDocument.Parse(sample.Read(round.Result!.Capture, "commands/version/command.json"));
        await Assert.That(command.RootElement.GetProperty("exitCode").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(command.RootElement.GetProperty("error").GetString()).IsNotNull();
        await Assert.That(command.RootElement.GetProperty("argv")[0].GetString()).IsEqualTo("--version");
    }

    [Test]
    public async Task ZeroExitWithTruncatedPackIsRecordedAsIncomplete()
    {
        if (OperatingSystem.IsWindows()) return;
        using var sample = new GitSample();
        sample.Write("body.txt", "body");
        sample.Commit("first");
        var prefix = GitSample.Run(sample.Repository, "bundle", "create", "-", "--all")[..^20];
        var truncated = Path.Combine(sample.DirectoryPath, "truncated.bundle");
        File.WriteAllBytes(truncated, prefix);
        var executable = sample.BundleProgram($"cat '{truncated}'\nexit 0\n");
        var round = await new GitCollector(sample.StoreDirectory, "tests", executable).ScanAsync(sample.Repository);
        await Assert.That(round.Failure).IsNotNull();
        using var saved = sample.Store.Open(round.Result!.Capture, "repository.bundle");
        using var bytes = new MemoryStream();
        saved.CopyTo(bytes);
        await Assert.That(bytes.ToArray().SequenceEqual(prefix)).IsTrue();
        using var verify = JsonDocument.Parse(sample.Read(round.Result.Capture, "commands/bundle-verify/command.json"));
        await Assert.That(verify.RootElement.GetProperty("exitCode").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task ParentExitWithAnInheritedPipeHasBoundedDrainAndPreservesItsPrefix()
    {
        if (OperatingSystem.IsWindows()) return;
        using var sample = new GitSample();
        sample.Write("body.txt", "body");
        sample.Commit("first");
        var pidFile = Path.Combine(sample.DirectoryPath, "orphan");
        var executable = sample.BundleProgram($$"""
            printf 'bundle-prefix'
            sleep 60 &
            printf '%s\n' "$!" > '{{pidFile}}'
            exit 0
            """);
        using var stop = new CancellationTokenSource();
        var running = new GitCollector(sample.StoreDirectory, "tests", executable).ScanAsync(sample.Repository, stop.Token);
        Process? child = null;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(pidFile)) await Task.Delay(10, deadline.Token);
            child = Process.GetProcessById(int.Parse((await File.ReadAllTextAsync(pidFile, deadline.Token)).Trim(), System.Globalization.CultureInfo.InvariantCulture));
            await Task.Delay(100, deadline.Token);
            stop.Cancel();
            var round = await running.WaitAsync(TimeSpan.FromSeconds(8));
            await Assert.That(round.Failure).IsNotNull();
            await Assert.That(sample.Read(round.Result!.Capture, "repository.bundle")).IsEqualTo("bundle-prefix");
            using var command = JsonDocument.Parse(sample.Read(round.Result.Capture, "commands/bundle/command.json"));
            await Assert.That(command.RootElement.GetProperty("cancelled").GetBoolean()).IsTrue();
            await Assert.That(command.RootElement.GetProperty("error").GetString()!.Contains("输出管道", StringComparison.Ordinal)).IsTrue();
        }
        finally
        {
            stop.Cancel();
            if (child is not null)
            {
                if (!child.HasExited) child.Kill();
                child.Dispose();
            }
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Test]
    public async Task BundleHeaderPointingToAnAbsentObjectIsRecordedAsIncomplete()
    {
        if (OperatingSystem.IsWindows()) return;
        using var sample = new GitSample();
        sample.Write("body.txt", "body");
        var commit = sample.Commit("first");
        var bundle = GitSample.Run(sample.Repository, "bundle", "create", "-", "--all");
        var offset = bundle.AsSpan().IndexOf(Encoding.ASCII.GetBytes(commit));
        Encoding.ASCII.GetBytes(new string('f', commit.Length)).CopyTo(bundle.AsSpan(offset));
        var invalid = Path.Combine(sample.DirectoryPath, "invalid-reference.bundle");
        File.WriteAllBytes(invalid, bundle);
        var executable = sample.BundleProgram($"cat '{invalid}'\nexit 0\n");
        var round = await new GitCollector(sample.StoreDirectory, "tests", executable).ScanAsync(sample.Repository);
        await Assert.That(round.Failure).IsNotNull();
        using var verify = JsonDocument.Parse(sample.Read(round.Result!.Capture, "commands/bundle-verify/command.json"));
        await Assert.That(verify.RootElement.GetProperty("exitCode").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task WatchStartsImmediatelyThenCapturesLaterHistoryAndStopsWhenCancelled()
    {
        using var sample = new GitSample();
        sample.Write("body.txt", "first\n");
        var first = sample.Commit("first");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var rounds = sample.Collector.Watch(sample.Repository, TimeSpan.FromMilliseconds(2), stop.Token).GetAsyncEnumerator();
        await Assert.That(await rounds.MoveNextAsync()).IsTrue();
        var captureA = rounds.Current.Result!.Capture;
        sample.Write("body.txt", "second\n");
        var second = sample.Commit("second");
        await Assert.That(await rounds.MoveNextAsync()).IsTrue();
        var captureB = rounds.Current.Result!.Capture;
        await Assert.That(sample.Read(captureA, "commands/head-before-object/stdout").Trim()).IsEqualTo(first);
        await Assert.That(sample.Read(captureB, "commands/head-before-object/stdout").Trim()).IsEqualTo(second);
        stop.Cancel();
        await Assert.That(async () => await rounds.MoveNextAsync()).Throws<OperationCanceledException>();
    }
}

internal sealed class GitSample : IDisposable
{
    private static readonly string[] Configuration = ["-c", "commit.gpgSign=false", "-c", "tag.gpgSign=false", "-c", "core.hooksPath=/dev/null"];
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "orisumi-git-tests", Guid.NewGuid().ToString("N"));
    public string Repository => Path.Combine(DirectoryPath, "source");
    public string StoreDirectory => Path.Combine(DirectoryPath, "store");
    public RawStore Store => new(StoreDirectory);
    public GitCollector Collector => new(StoreDirectory, "tests");

    public GitSample()
    {
        Directory.CreateDirectory(Repository);
        Git("init", "--template=", "-b", "main");
    }

    public void Write(string path, string content)
    {
        var file = Path.Combine(Repository, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content, new UTF8Encoding(false));
    }

    public string Commit(string message)
    {
        Git("add", "--all");
        Git("commit", "-m", message);
        return Git("rev-parse", "HEAD");
    }

    public string Git(params string[] args) => GitAt(Repository, args);
    public static string GitAt(string cwd, params string[] args) => Encoding.UTF8.GetString(Run(cwd, args)).TrimEnd('\r', '\n');

    public static byte[] Run(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in Configuration.Concat(args))
            start.ArgumentList.Add(argument);
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.Ordinal)).ToArray()) start.Environment.Remove(key);
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        start.Environment["GIT_AUTHOR_NAME"] = start.Environment["GIT_COMMITTER_NAME"] = "Git fixture";
        start.Environment["GIT_AUTHOR_EMAIL"] = start.Environment["GIT_COMMITTER_EMAIL"] = "fixture@example.invalid";
        using var process = Process.Start(start)!;
        using var output = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        var error = process.StandardError.ReadToEndAsync();
        Task.WhenAll(copy, error, process.WaitForExitAsync()).GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {error.Result}");
        return output.ToArray();
    }

    public string Read(Capture capture, string path)
    {
        using var reader = new StreamReader(Store.Open(capture, path), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public string BundleProgram(string body)
    {
        var script = Path.Combine(DirectoryPath, "git-fixture");
        File.WriteAllText(script, "#!/bin/sh\nif [ \"$1\" = -c ] && [ \"$2\" = pack.threads=1 ]; then shift 2; fi\nif [ \"$1\" = bundle ] && [ \"$2\" = create ]; then\n" + body + "\nelse\nexec git \"$@\"\nfi\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    public string Restore(Capture capture, string name)
    {
        var originals = Path.Combine(DirectoryPath, "originals-" + name);
        new CaptureRecovery(Store).Restore(capture, originals);
        var restored = Path.Combine(DirectoryPath, "restored-" + name);
        GitAt(DirectoryPath, "clone", "--mirror", "--template=", Path.Combine(originals, "files", "repository.bundle"), restored);
        GitAt(restored, "fsck", "--full", "--strict");
        return restored;
    }

    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}
