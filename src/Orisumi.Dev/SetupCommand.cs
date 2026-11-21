using System.CommandLine;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Orisumi.Dev;

internal static class SetupCommand
{
    internal static Command Create()
    {
        var setup = new Command("setup", "检查前置条件，安装依赖，构建并安装全局 orisumi-dev；可重复运行。");
        setup.SetAction((_, token) => Run(Repository.Require(), token));
        return setup;
    }

    private static async Task<int> Run(string root, CancellationToken token)
    {
        await CheckPrerequisites(root, token);
        var docs = Path.Combine(root, DevCommand.DocsDirectory);
        var extension = Path.Combine(root, DevCommand.ExtensionDirectory);
        List<(string Executable, string Directory, string[] Arguments)> steps =
        [
            ("dotnet", root, ["build", "Orisumi.slnx"]),
            ("npm", docs, ["ci"]),
            ("npm", extension, ["ci"]),
            ("dotnet", root, ["pack", "src/Orisumi.Dev", "--configuration", "Release", "--output", "bin/packages"]),
        ];
        foreach (var (executable, directory, arguments) in steps)
        {
            var exitCode = await Development.Run(executable, directory, token, arguments);
            if (exitCode != 0)
                return exitCode;
        }
        var extensionCode = await ExtensionBuild.BuildDevelopment(root, token);
        if (extensionCode != 0) return extensionCode;
        // A same-version tool update leaves the old launcher installed.
        foreach (var package in new[] { "Orisumi.Cli", "Orisumi.Dev" })
            if (await ToolInstalled(package, token))
            {
                var code = await Development.Run("dotnet", root, token, "tool", "uninstall", "--global", package);
                if (code != 0) return code;
            }
        return await Development.Run("dotnet", root, token, "tool", "install", "--global", "Orisumi.Dev", "--source", "bin/packages");
    }

    private static async Task CheckPrerequisites(string root, CancellationToken token)
    {
        var missing = new List<string>();
        // Command Line Tools lack SwiftUI's macro plugins, so the GUI only builds with Xcode selected.
        var developer = await Output("xcode-select", token, "-p");
        if (developer is null || !developer.Trim().EndsWith(".app/Contents/Developer", StringComparison.Ordinal))
            missing.Add($"Xcode：安装后运行 sudo xcode-select -s /Applications/Xcode.app（当前 {developer?.Trim() ?? "未安装"}）。");

        var required = RequiredNode(root);
        var node = await Output("node", token, "--version");
        if (node is null || !Version.TryParse(node.Trim().TrimStart('v'), out var version) || version < required)
            missing.Add($"Node.js {required}+：{(node is null ? "未安装" : $"当前 {node.Trim()}")}。");

        var home = Environment.GetEnvironmentVariable("DOTNET_CLI_HOME") ??
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var tools = Path.Combine(home, ".dotnet", "tools");
        var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        if (!path.Any(entry => entry.Length > 0 && Path.TrimEndingDirectorySeparator(entry) == tools))
            missing.Add($"PATH 中缺少全局工具目录 {tools}。");

        if (missing.Count > 0)
            throw new IOException(string.Join(Environment.NewLine + "  ", ["缺少前置条件：", .. missing]));
    }

    private static async Task<bool> ToolInstalled(string package, CancellationToken token)
    {
        // Exits non-zero when the package is not installed.
        var list = await Output("dotnet", token, "tool", "list", "--global", package, "--format", "json");
        if (list is null)
            return false;
        using var json = JsonDocument.Parse(list);
        return json.RootElement.GetProperty("data").GetArrayLength() > 0;
    }

    // The docs site declares the Node.js floor for the whole checkout.
    private static Version RequiredNode(string root)
    {
        using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, DevCommand.DocsDirectory, "package.json")));
        var range = package.RootElement.GetProperty("engines").GetProperty("node").GetString() ?? "";
        return range.StartsWith(">=", StringComparison.Ordinal) && Version.TryParse(range[2..], out var version)
            ? version
            : throw new InvalidDataException($"无法识别文档站 engines.node：{range}。");
    }

    private static async Task<string?> Output(string executable, CancellationToken token, params string[] arguments)
    {
        try
        {
            var result = await Process.RunAndCaptureTextAsync(executable, arguments, token);
            if (result.ExitStatus.Canceled) throw new OperationCanceledException(token);
            return result.ExitStatus.ExitCode == 0 ? result.StandardOutput : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }
}
