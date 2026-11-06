using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Orisumi.Raw;

namespace Orisumi.Capturing;

public sealed class GitCollector
{
    private readonly string _storeDirectory;
    private readonly string _collector;
    private readonly string _git;

    public GitCollector(string storeDirectory, string collector) : this(storeDirectory, collector, "git") { }

    internal GitCollector(string storeDirectory, string collector, string git)
    {
        _storeDirectory = storeDirectory;
        _collector = collector;
        _git = git;
    }

    public async Task<CaptureRound> ScanAsync(string repository, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        repository = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository));
        CaptureWriter? writer = null;
        string? temporary = null;
        try
        {
            writer = new RawStore(_storeDirectory).BeginCapture(new CaptureStart("git", _collector, repository), CancellationToken.None);
            temporary = Directory.CreateTempSubdirectory("orisumi-git-").FullName;
            var run = new GitRun(writer, _git, repository, temporary);
            try
            {
                await run.CaptureAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                run.Failures.Add("Git 采集已取消；已取得的命令输出随本轮保存");
            }
            var failure = run.Failures.Count == 0 ? null : new CaptureFailure(string.Join('\n', run.Failures));
            return new CaptureRound(writer.Complete(failure));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            var reason = $"原件存储失败：{error.Message}";
            try
            {
                if (writer is not null) return new CaptureRound(writer.Fail(reason));
            }
            catch (Exception recordingError) when (recordingError is IOException or UnauthorizedAccessException)
            {
                reason += $"；失败记录也无法保存：{recordingError.Message}";
            }
            return new CaptureRound(null, reason);
        }
        finally
        {
            if (temporary is not null) Directory.Delete(temporary, recursive: true);
        }
    }

    public async IAsyncEnumerable<CaptureRound> Watch(string repository, TimeSpan interval,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        if (interval <= TimeSpan.Zero || interval.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(interval), "扫描间隔必须大于零且不超过约 49 天");
        while (true)
        {
            token.ThrowIfCancellationRequested();
            yield return await ScanAsync(repository, token).ConfigureAwait(false);
            await Task.Delay(interval, token).ConfigureAwait(false);
        }
    }

    private sealed class GitRun(CaptureWriter writer, string git, string repository, string temporary)
    {
        private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);
        private const string PipeFailure = "Git 主进程已退出，输出管道仍未关闭；已停止读取并保存前缀，已重父后代的回收情况未知";
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
        private static readonly Dictionary<string, string> EnvironmentOverrides = new(StringComparer.Ordinal)
        {
            ["GIT_OPTIONAL_LOCKS"] = "0",
            ["GIT_NO_LAZY_FETCH"] = "1",
            ["GIT_NO_REPLACE_OBJECTS"] = "1",
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_ALLOW_PROTOCOL"] = "",
        };

        public List<string> Failures { get; } = [];

        public async Task CaptureAsync(CancellationToken token)
        {
            if (!(await CommandAsync("version", ["--version"], token).ConfigureAwait(false)).Succeeded) return;
            if (!(await CommandAsync("git-directory", ["rev-parse", "--absolute-git-dir"], token).ConfigureAwait(false)).Succeeded) return;
            await CommandAsync("common-directory", ["rev-parse", "--path-format=absolute", "--git-common-dir"], token).ConfigureAwait(false);
            var format = await CommandAsync("object-format", ["rev-parse", "--show-object-format"], token).ConfigureAwait(false);
            var shallow = await CommandAsync("shallow", ["rev-parse", "--is-shallow-repository"], token).ConfigureAwait(false);
            if (shallow.Text == "true") Failures.Add("来源是 shallow 仓库；本轮对象范围有历史截断");
            await CommandAsync("local-config", ["config", "--local", "--no-includes", "--null", "--show-origin", "--show-scope", "--list"], token).ConfigureAwait(false);
            await ReferencesAsync("before", token).ConfigureAwait(false);
            var bundle = await CommandAsync("bundle", ["-c", "pack.threads=1", "bundle", "create", "-", "--all"], token, bundle: true).ConfigureAwait(false);
            await ReferencesAsync("after", token).ConfigureAwait(false);
            if (!bundle.Succeeded) return;
            if (new FileInfo(bundle.Path).Length == 0)
            {
                Failures.Add("Git bundle 命令成功退出但没有交付字节");
                return;
            }
            await CommandAsync("bundle-heads", ["bundle", "list-heads", bundle.Path], token, cwd: temporary).ConfigureAwait(false);
            if (!format.Succeeded) return;
            var verification = Path.Combine(temporary, "verification.git");
            var initialized = await CommandAsync("verify-init", ["init", "--bare", "--template=", $"--object-format={format.Text}", verification], token, cwd: temporary).ConfigureAwait(false);
            if (!initialized.Succeeded) return;
            var verified = await CommandAsync("bundle-verify", ["bundle", "verify", bundle.Path], token, cwd: verification).ConfigureAwait(false);
            if (!verified.Succeeded) return;
            var recovered = Path.Combine(temporary, "recovered.git");
            var cloned = await CommandAsync("bundle-clone", ["clone", "--mirror", "--template=", bundle.Path, recovered], token, cwd: temporary).ConfigureAwait(false);
            if (cloned.Succeeded)
                await CommandAsync("bundle-fsck", ["fsck", "--full", "--strict"], token, cwd: recovered).ConfigureAwait(false);
        }

        private async Task ReferencesAsync(string phase, CancellationToken token)
        {
            await CommandAsync($"refs-{phase}", ["for-each-ref", "--format=%(objectname)%00%(refname)%00%(symref)%00"], token).ConfigureAwait(false);
            await CommandAsync($"head-{phase}-symbolic", ["symbolic-ref", "--quiet", "HEAD"], token, accepted: [0, 1]).ConfigureAwait(false);
            await CommandAsync($"head-{phase}-object", ["rev-parse", "--verify", "HEAD"], token, accepted: [0, 128]).ConfigureAwait(false);
        }

        private async Task<CommandResult> CommandAsync(string name, string[] argv, CancellationToken token,
            string? cwd = null, int[]? accepted = null, bool bundle = false)
        {
            token.ThrowIfCancellationRequested();
            var prefix = $"commands/{name}";
            var stdoutPath = bundle ? "repository.bundle" : $"{prefix}/stdout";
            var stderrPath = $"{prefix}/stderr";
            var stdout = Path.Combine(temporary, stdoutPath);
            var stderr = Path.Combine(temporary, stderrPath);
            Directory.CreateDirectory(Path.GetDirectoryName(stdout)!);
            Directory.CreateDirectory(Path.GetDirectoryName(stderr)!);
            var start = new ProcessStartInfo(git)
            {
                WorkingDirectory = cwd ?? repository,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in argv) start.ArgumentList.Add(argument);
            var removedEnvironment = start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
            foreach (var key in removedEnvironment) start.Environment.Remove(key);
            var environment = new Dictionary<string, string>(EnvironmentOverrides, StringComparer.Ordinal);
            if (name == "bundle-clone") environment["GIT_ALLOW_PROTOCOL"] = "file";
            foreach (var (key, value) in environment) start.Environment[key] = value;
            var startedAt = DateTimeOffset.UtcNow;
            int? exitCode = null;
            string? error = null;
            var cancelled = false;
            using (var process = new Process { StartInfo = start })
            await using (var output = new FileStream(stdout, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 81920, FileOptions.Asynchronous))
            await using (var errors = new FileStream(stderr, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 81920, FileOptions.Asynchronous))
            {
                using var drain = new CancellationTokenSource();
                var started = false;
                var drainExpired = false;
                Task copyOutput = Task.CompletedTask;
                Task copyError = Task.CompletedTask;
                try
                {
                    started = process.Start();
                    if (!started) throw new IOException("Git 进程没有启动");
                    process.StandardInput.Close();
                    copyOutput = process.StandardOutput.BaseStream.CopyToAsync(output, drain.Token);
                    copyError = process.StandardError.BaseStream.CopyToAsync(errors, drain.Token);
                    var exited = process.WaitForExitAsync(CancellationToken.None);
                    var pending = new List<Task> { exited, copyOutput, copyError };
                    while (pending.Count != 0)
                    {
                        var completed = await Task.WhenAny(pending).WaitAsync(token).ConfigureAwait(false);
                        await completed.ConfigureAwait(false);
                        pending.Remove(completed);
                        if (completed != exited) continue;
                        await Task.WhenAll(copyOutput, copyError).WaitAsync(DrainTimeout, token).ConfigureAwait(false);
                        break;
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    cancelled = true;
                }
                catch (TimeoutException)
                {
                    drainExpired = true;
                    error = PipeFailure;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
                {
                    error = exception.Message;
                }
                finally
                {
                    if (started)
                    {
                        try
                        {
                            if (!process.HasExited) process.Kill(entireProcessTree: true);
                        }
                        catch (Exception exception) when ((exception is InvalidOperationException or Win32Exception) && process.HasExited)
                        {
                        }
                        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                        exitCode = process.ExitCode;
                        try
                        {
                            if (!drainExpired)
                                await Task.WhenAll(copyOutput, copyError).WaitAsync(DrainTimeout, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (TimeoutException)
                        {
                            drainExpired = true;
                            error = error is null ? PipeFailure : $"{error}；{PipeFailure}";
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
                        {
                            error ??= exception.Message;
                        }
                        if (drainExpired)
                        {
                            await drain.CancelAsync().ConfigureAwait(false);
                            process.StandardOutput.Dispose();
                            process.StandardError.Dispose();
                            try
                            {
                                await Task.WhenAll(copyOutput, copyError).ConfigureAwait(false);
                            }
                            catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
                            {
                            }
                        }
                    }
                }
            }
            var completedAt = DateTimeOffset.UtcNow;
            Save(stdoutPath, stdout, bundle ? "git-bundle" : "git-command-stdout");
            Save(stderrPath, stderr, "git-command-stderr");
            using var metadata = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new
            {
                format = "orisumi-git-command-v1",
                executable = git,
                argv,
                cwd = start.WorkingDirectory,
                environment,
                removedEnvironment,
                startedAt,
                completedAt,
                exitCode,
                error,
                cancelled,
                stdout = stdoutPath,
                stderr = stderrPath,
            }, JsonOptions));
            writer.Add($"{prefix}/command.json", repository, metadata, "git-command-metadata");
            if (cancelled)
            {
                if (error is not null) Failures.Add($"Git {name}：{error}；原始输出见 {prefix}");
                throw new OperationCanceledException(token);
            }
            var succeeded = error is null && exitCode is { } code && (accepted ?? [0]).Contains(code);
            if (!succeeded) Failures.Add($"Git {name}：{error ?? $"退出码为 {exitCode}"}；原始输出见 {prefix}");
            return new CommandResult(succeeded, stdout, bundle ? "" : File.ReadAllText(stdout).TrimEnd('\r', '\n'));
        }

        private void Save(string path, string file, string method)
        {
            using var bytes = File.OpenRead(file);
            writer.Add(path, repository, bytes, method);
        }

        private sealed record CommandResult(bool Succeeded, string Path, string Text);
    }
}
