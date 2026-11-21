using System.CommandLine;

namespace Orisumi.Dev;

internal static class DevCommand
{
    internal const string DocsDirectory = "Docs";
    internal const string ExtensionDirectory = "Collectors/Browser/Extension";

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
        docs.SetAction((_, token) => Development.Run("npm", Path.Combine(Repository.Require(), DocsDirectory), token, "run", "dev"));
        AddNpmCommand(docs, "install", "安装锁定版本的文档站依赖。", DocsDirectory, "ci");
        AddNpmCommand(docs, "check", "检查文档站类型和路由。", DocsDirectory, "run", "types:check");
        AddNpmCommand(docs, "build", "生成文档站生产构建。", DocsDirectory, "run", "build");
        AddNpmCommand(docs, "start", "运行已构建的文档站：http://127.0.0.1:3020/docs。", DocsDirectory, "start");

        return new Command("dev", "环境安装、构建、文档站与行数统计。") { SetupCommand.Create(), build, docs, Loc.LocCommand.Create() };
    }

    internal static Command Extension()
    {
        var extension = new Command("extension", "构建 Chrome 扩展。");
        AddNpmCommand(extension, "install", "安装锁定版本的扩展开发依赖。", ExtensionDirectory, "ci");
        var build = new Command("build", "构建当前开发实例的扩展。");
        build.SetAction((_, token) => ExtensionBuild.BuildDevelopment(Repository.Require(), token));
        extension.Add(build);
        return extension;
    }

    private static void AddNpmCommand(Command parent, string name, string description, string directory, params string[] arguments)
    {
        var command = new Command(name, description);
        command.SetAction((_, token) => Development.Run("npm", Path.Combine(Repository.Require(), directory), token, arguments));
        parent.Add(command);
    }
}
