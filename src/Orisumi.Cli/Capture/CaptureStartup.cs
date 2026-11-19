using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Orisumi.Collectors.Browser;
using Orisumi.Platform;

namespace Orisumi.Cli.Capture;

internal static class CaptureStartup
{
    internal static async Task Start(string store, string? extensionId, TextWriter output, CancellationToken cancellation)
    {
        var root = Repository.Require();
        var location = new Store(store);
        store = location.Root;
        var assembly = typeof(CaptureStartup).Assembly.Location;
        var dotnet = SourceLauncher.Dotnet;
        var logs = location.Logs;
        var agents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/LaunchAgents");
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(location.State);
        Directory.CreateDirectory(agents);
        // Serialize startup across stores too: the LaunchAgent labels belong to this user.
        using var startupLock = new FileStream(Path.Combine(agents, "dev.orisumi.capture.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (extensionId is not null)
            output.WriteLine(ChromeNativeHost.Register(store, extensionId, Path.Combine(AppContext.BaseDirectory, "Orisumi")));

        string[] foreground = [dotnet, assembly, "--store", store, "foreground", "--windows", "--input", "--microphone", "--audio-output", "--camera"];
        var pidFile = Path.Combine(location.State, "foreground.pid");
        await StopForeground(pidFile, cancellation);
        cancellation.ThrowIfCancellationRequested();
        // Keep the caller's permission context. Native spawn redirects directly to files,
        // so logging and the collector survive after this short-lived command exits.
        var pid = DetachedProcess.Start(foreground, Path.Combine(logs, "foreground.out.log"), Path.Combine(logs, "foreground.err.log"));
        await Task.Delay(500, CancellationToken.None);
        if (DetachedProcess.HasExited(pid)) throw new IOException($"前台采集启动失败，请查看 {Path.Combine(logs, "foreground.err.log")}。");
        WritePidFile(pidFile, pid);
        output.WriteLine($"Started foreground collector: pid {pid}");

        var domain = $"gui/{getuid()}";
        foreach (var source in CaptureSources.All)
        {
            if (source is not { Name: var name, Agent: { } arguments }) continue;
            cancellation.ThrowIfCancellationRequested();
            var label = $"dev.orisumi.capture.{name}";
            // A missing service is normal on first installation. Bootstrap reports real failures.
            await Run("/bin/launchctl", ["bootout", $"{domain}/{label}"], cancellation, allowFailure: true);
            // bootout can return before launchd has removed the service; bootstrap then fails with an I/O error.
            var removal = Stopwatch.StartNew();
            while (await Run("/bin/launchctl", ["print", $"{domain}/{label}"], cancellation, allowFailure: true) == 0)
            {
                if (removal.Elapsed > TimeSpan.FromSeconds(10)) throw new IOException($"{label} 未能在 10 秒内停止。");
                await Task.Delay(100, cancellation);
            }
            var path = Path.Combine(agents, label + ".plist");
            LaunchAgent(name, dotnet, assembly, root, store, arguments).Save(path);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await Run("/bin/launchctl", ["enable", $"{domain}/{label}"], cancellation);
            await Run("/bin/launchctl", ["bootstrap", domain, path], cancellation);
            output.WriteLine($"Started {label}");
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
            new XElement("key", "StandardOutPath"), new XElement("string", Path.Combine(new Store(store).Logs, name + ".out.log")),
            new XElement("key", "StandardErrorPath"), new XElement("string", Path.Combine(new Store(store).Logs, name + ".err.log")));
        dictionary.Add(new XElement("key", "KeepAlive"), new XElement("true"),
            new XElement("key", "ThrottleInterval"), new XElement("integer", 10));
        return new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"), dictionary));
    }

    // PID and start time together identify the process; a reused PID has a different start time.
    internal static void WritePidFile(string pidFile, int pid)
    {
        using var process = Process.GetProcessById(pid);
        File.WriteAllText(pidFile, string.Create(CultureInfo.InvariantCulture, $"{pid} {process.StartTime.ToUniversalTime().Ticks}"));
    }

    internal static async Task StopForeground(string pidFile, CancellationToken cancellation)
    {
        if (!File.Exists(pidFile) || File.ReadAllText(pidFile).Split(' ') is not [var pidText, var startText] ||
            !int.TryParse(pidText, CultureInfo.InvariantCulture, out var pid) ||
            !long.TryParse(startText, CultureInfo.InvariantCulture, out var started)) return;
        Process process;
        try { process = Process.GetProcessById(pid); }
        catch (ArgumentException) { return; }
        using (process)
        {
            try { if (process.StartTime.ToUniversalTime().Ticks != started) return; }
            catch (InvalidOperationException) { return; } // exited meanwhile

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

    private static async Task<int> Run(string executable, string[] arguments, CancellationToken cancellation, bool allowFailure = false)
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
        await stdout;
        var error = await stderr;
        if (process.ExitCode != 0 && !allowFailure) throw new IOException($"{Path.GetFileName(executable)}: {error.Trim()}");
        return process.ExitCode;
    }

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern uint getuid();
}
