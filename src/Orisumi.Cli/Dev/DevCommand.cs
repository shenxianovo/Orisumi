using System.CommandLine;

namespace Orisumi.Cli.Dev;

internal static class DevCommand
{
    internal static Command Create()
    {
        var configuration = new Option<string>("--configuration", "-c")
        {
            Description = ".NET 构建配置。",
            DefaultValueFactory = _ => "Debug",
        };
        configuration.AcceptOnlyFromAmong("Debug", "Release");
        var build = new Command("build", "构建 .NET 解决方案（包含 macOS 原生采集器）。") { configuration };
        build.SetAction((result, token) => Development.Run("dotnet", Repository.Require(), token,
            "build", "Orisumi.slnx", "--configuration", result.GetRequiredValue(configuration)));

        var docs = new Command("docs", "启动本地文档站 http://127.0.0.1:3020/docs，或管理文档站。");
        docs.SetAction((_, token) => Development.Run("npm", Path.Combine(Repository.Require(), "src/Orisumi.Docs"), token, "run", "dev"));
        AddNpmCommand(docs, "install", "安装锁定版本的文档站依赖。", "src/Orisumi.Docs", "ci");
        AddNpmCommand(docs, "check", "检查文档站类型和路由。", "src/Orisumi.Docs", "run", "types:check");
        AddNpmCommand(docs, "build", "生成文档站生产构建。", "src/Orisumi.Docs", "run", "build");
        AddNpmCommand(docs, "start", "运行已构建的文档站：http://127.0.0.1:3020/docs。", "src/Orisumi.Docs", "start");

        var browser = new Command("browser", "构建 Chrome 扩展。");
        const string extensionDirectory = "src/Collectors/Orisumi.Collectors.Browser/Extension";
        AddNpmCommand(browser, "install", "安装锁定版本的扩展开发依赖。", extensionDirectory, "ci");
        AddNpmCommand(browser, "build", "构建扩展；随后在 Chrome 扩展页重新加载。", extensionDirectory, "run", "build");
        return new Command("dev", "构建、文档站、扩展开发与行数统计。") { build, docs, browser, Loc.LocCommand.Create() };
    }

    private static void AddNpmCommand(Command parent, string name, string description, string directory, params string[] arguments)
    {
        var command = new Command(name, description);
        command.SetAction((_, token) => Development.Run("npm", Path.Combine(Repository.Require(), directory), token, arguments));
        parent.Add(command);
    }
}
