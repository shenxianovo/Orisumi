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
        var build = new Command("build", "构建当前平台的宿主、开发工具与采集器。") { configuration };
        build.SetAction(async (result, token) =>
        {
            var root = Repository.Require();
            foreach (var target in DevPlatform.BuildTargets(root))
            {
                var code = await Development.Run("dotnet", root, token, "build", target,
                    "--configuration", result.GetRequiredValue(configuration));
                if (code != 0) return code;
            }
            return 0;
        });

        var docs = new Command("docs", "启动本地文档站 http://127.0.0.1:3020/docs，或管理文档站。");
        docs.SetAction((_, token) => Development.Run("npm", Path.Combine(Repository.Require(), DocsDirectory), token, "run", "dev"));
        AddNpmCommand(docs, "install", "安装锁定版本的文档站依赖。", DocsDirectory, "ci");
        AddNpmCommand(docs, "check", "检查文档站类型和路由。", DocsDirectory, "run", "types:check");
        AddNpmCommand(docs, "build", "生成文档站生产构建。", DocsDirectory, "run", "build");
        AddNpmCommand(docs, "start", "运行已构建的文档站：http://127.0.0.1:3020/docs。", DocsDirectory, "start");

        return new Command("dev", "环境安装、构建、文档站与行数统计。") { SetupCommand.Create(), build, docs, Loc.LocCommand.Create() };
    }

    private static void AddNpmCommand(Command parent, string name, string description, string directory, params string[] arguments)
    {
        var command = new Command(name, description);
        command.SetAction((_, token) => Development.Run("npm", Path.Combine(Repository.Require(), directory), token, arguments));
        parent.Add(command);
    }
}
