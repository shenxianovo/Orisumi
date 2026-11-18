using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Orisumi.Collectors.Browser;

namespace Orisumi.Cli.Capture;

internal static class CaptureStartup
{
    internal static async Task Start(string store, string? extensionId, TextWriter output, CancellationToken cancellation)
    {
        var root = Repository.Require();
        store = Path.GetFullPath(store);
        var assembly = typeof(CaptureStartup).Assembly.Location;
        var dotnet = SourceLauncher.Dotnet;
        var logs = Path.Combine(store, "logs");
        var agents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/LaunchAgents");
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(agents);
        // Serialize startup across stores too: the LaunchAgent labels belong to this user.
        using var startupLock = new FileStream(Path.Combine(agents, "dev.orisumi.capture.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (extensionId is not null)
            output.WriteLine(ChromeNativeHost.Register(store, extensionId, Path.Combine(AppContext.BaseDirectory, "Orisumi")));

        string[] foreground = [dotnet, assembly, "--store", store, "foreground", "--windows", "--input"];
        var pidFile = Path.Combine(store, "foreground.pid");
        await StopForeground(pidFile, foreground, cancellation);
        cancellation.ThrowIfCancellationRequested();
        // Keep the caller's permission context. Native spawn redirects directly to files,
        // so logging and the collector survive after this short-lived command exits.
        var pid = DetachedProcess.Start(foreground, Path.Combine(logs, "foreground.out.log"), Path.Combine(logs, "foreground.err.log"));
        File.WriteAllText(pidFile, pid.ToString(CultureInfo.InvariantCulture));
        await Task.Delay(500, CancellationToken.None);
        if (DetachedProcess.HasExited(pid)) throw new IOException($"前台采集启动失败，请查看 {Path.Combine(logs, "foreground.err.log")}。");
        output.WriteLine($"Started foreground collector: pid {pid}");

        var domain = $"gui/{getuid()}";
        foreach (var (name, arguments) in new (string, string[])[]
        {
            ("codex", ["codex", "capture", "--all", "--watch"]),
            ("claude-code", ["claude", "capture", "--watch"]),
            ("network", ["network", "capture"]),
        })
        {
            cancellation.ThrowIfCancellationRequested();
            var label = $"dev.orisumi.capture.{name}";
            // A missing service is normal on first installation. Bootstrap reports real failures.
            await Run("/bin/launchctl", ["bootout", $"{domain}/{label}"], cancellation, allowFailure: true);
            var path = Path.Combine(agents, label + ".plist");
            LaunchAgent(name, dotnet, assembly, root, store, arguments).Save(path);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await Run("/bin/launchctl", ["enable", $"{domain}/{label}"], cancellation);
            await Run("/bin/launchctl", ["bootstrap", domain, path], cancellation);
            output.WriteLine($"Started {label}: {(name == "network" ? "continuous, samples every 5 seconds" : "watching source changes")}");
        }
    }

    internal static XDocument LaunchAgent(string name, string dotnet, string assembly, string root, string store, string[] arguments)
    {
        var dictionary = new XElement("dict",
            new XElement("key", "Label"), new XElement("string", $"dev.orisumi.capture.{name}"),
            new XElement("key", "ProgramArguments"), new XElement("array",
                new[] { dotnet, assembly, "--store", store }.Concat(arguments).Select(value => new XElement("string", value))),
            new XElement("key", "WorkingDirectory"), new XElement("string", root),
            new XElement("key", "RunAtLoad"), new XElement("true"),
            new XElement("key", "StandardOutPath"), new XElement("string", Path.Combine(store, "logs", name + ".out.log")),
            new XElement("key", "StandardErrorPath"), new XElement("string", Path.Combine(store, "logs", name + ".err.log")));
        dictionary.Add(new XElement("key", "KeepAlive"), new XElement("true"),
            new XElement("key", "ThrottleInterval"), new XElement("integer", 10));
        return new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"), dictionary));
    }

    internal static async Task StopForeground(string pidFile, string[] expectedCommand, CancellationToken cancellation)
    {
        if (!File.Exists(pidFile) || !int.TryParse(File.ReadAllText(pidFile), out var pid) || pid <= 0) return;
        var previous = await Run("/bin/ps", ["-ww", "-p", pid.ToString(CultureInfo.InvariantCulture), "-o", "command="], cancellation, allowFailure: true);
        // An old pid file may refer to a reused PID. Only stop our exact command and store.
        if (previous.Trim() != string.Join(' ', expectedCommand)) return;
        Process process;
        try { process = Process.GetProcessById(pid); }
        catch (ArgumentException) { return; }
        using (process)
        {
            await Run("/bin/kill", ["-TERM", pid.ToString(CultureInfo.InvariantCulture)], cancellation, allowFailure: true);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                throw new IOException("上一次前台采集尚未退出，未启动重复采集器。");
            }
        }
    }

    private static async Task<string> Run(string executable, string[] arguments, CancellationToken cancellation, bool allowFailure = false)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException($"无法启动 {executable}。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(cancellation); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        var result = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0 && !allowFailure) throw new IOException($"{Path.GetFileName(executable)}: {error.Trim()}");
        return result;
    }

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern uint getuid();
}
