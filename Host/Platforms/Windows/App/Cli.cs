using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Orisumi.App;

/// Where the CLI and the instance are. Installed, the tray sits in `<store>\app\host\` next to `orisumi.exe`;
/// from a checkout, `--cli` and `--store` say which host and instance to read.
internal sealed record AppConfiguration(string Cli, string? Store, bool Installed)
{
    internal const string Usage = "Usage: orisumi-app [--cli <path-to-orisumi.exe>] [--store <directory>]";

    internal static AppConfiguration Parse(string[] arguments, string executable)
    {
        string? cli = null, store = null;
        for (var index = 0; index < arguments.Length; index += 2)
        {
            if (index + 1 >= arguments.Length || arguments[index + 1].Length == 0) throw new ArgumentException(Usage);
            var value = Path.GetFullPath(arguments[index + 1]);
            switch (arguments[index])
            {
                case "--cli" when cli is null: cli = value; break;
                case "--store" when store is null: store = value; break;
                default: throw new ArgumentException(Usage);
            }
        }
        var installed = InstalledStore(executable);
        // The installed path, not this file's directory: an update moves the running program's directory away.
        cli ??= installed is null ? throw new ArgumentException("Pass --cli when running from a checkout.\n\n" + Usage)
            : Path.Combine(installed, "app", "host", "orisumi.exe");
        return new(cli, store ?? installed, arguments.Length == 0 && installed is not null);
    }

    internal static string? InstalledStore(string executable)
    {
        var host = Path.GetDirectoryName(Path.GetFullPath(executable));
        var app = Path.GetDirectoryName(host);
        return string.Equals(Path.GetFileName(host), "host", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFileName(app), "app", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(app) : null;
    }

    internal string Executable => Path.Combine(Path.GetDirectoryName(Cli)!, "orisumi-app.exe");

    /// The instance name from `<store>\instance.json`; null when it can't be read yet.
    internal string? InstanceName()
    {
        if (Store is null) return null;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Store, "instance.json")));
            return json.RootElement.TryGetProperty("name", out var name) ? name.GetString() : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}

internal sealed class CliException(string message) : Exception(message);

internal sealed class Cli(AppConfiguration configuration)
{
    // Started without a window, the CLI gets a console of its own whose code page is the OEM one (936 on a
    // Chinese system), and .NET writes its output in that code page.
    private static readonly Encoding Output = CreateOutputEncoding();

    private static Encoding CreateOutputEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding((int)Native.GetOEMCP());
    }

    internal async Task<CaptureStatus> Status() => Presentation.Parse(await Run("status", "--json"));

    internal Task Capture(string action) => Run(action);

    private async Task<string> Run(params string[] capture)
    {
        var start = new ProcessStartInfo(configuration.Cli)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Output,
            StandardErrorEncoding = Output,
        };
        if (configuration.Store is { } store) { start.ArgumentList.Add("--store"); start.ArgumentList.Add(store); }
        start.ArgumentList.Add("capture");
        foreach (var argument in capture) start.ArgumentList.Add(argument);

        Process process;
        try { process = Process.Start(start) ?? throw new CliException($"Could not start {configuration.Cli}"); }
        catch (System.ComponentModel.Win32Exception error) { throw new CliException($"Can't run {Path.GetFileName(configuration.Cli)}: {error.Message}"); }
        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                // The CLI wraps its error at 80 columns when redirected; one line reads better in a menu or message box.
                var message = string.Join(' ', (await errors).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                if (message.StartsWith("orisumi: ", StringComparison.Ordinal)) message = message["orisumi: ".Length..];
                throw new CliException(message.Length > 0 ? message : $"orisumi capture {capture[0]} exited with code {process.ExitCode}");
            }
            return await output;
        }
    }
}
