using System.CommandLine;
using System.ComponentModel;
using Orisumi.Collectors.Browser;
using Orisumi.Dev;
using Spectre.Console;

var configuration = new Option<string>("--configuration", "-c")
{
    Description = ".NET 构建配置。",
    DefaultValueFactory = _ => "Debug",
};
configuration.AcceptOnlyFromAmong("Debug", "Release");
var build = new Command("build", "构建 .NET 解决方案（包含 macOS 原生采集器）。") { configuration };
build.SetAction((result, token) => Development.Run("dotnet", Development.RepositoryRoot(), token,
    "build", "Orisumi.slnx", "--configuration", result.GetRequiredValue(configuration)));

var docs = new Command("docs", "安装、运行和构建文档站。");
AddNpmCommand(docs, "install", "安装锁定版本的文档站依赖。", "src/Orisumi.Docs", "ci");
AddNpmCommand(docs, "dev", "启动本地文档站：http://127.0.0.1:3020/docs。", "src/Orisumi.Docs", "run", "dev");
AddNpmCommand(docs, "check", "检查文档站类型和路由。", "src/Orisumi.Docs", "run", "types:check");
AddNpmCommand(docs, "build", "生成文档站生产构建。", "src/Orisumi.Docs", "run", "build");
AddNpmCommand(docs, "start", "运行已构建的文档站：http://127.0.0.1:3020/docs。", "src/Orisumi.Docs", "start");

var browser = new Command("browser", "构建 Chrome 扩展并注册本机接收端。");
const string extensionDirectory = "src/Collectors/Orisumi.Collectors.Browser/Extension";
AddNpmCommand(browser, "install", "安装锁定版本的扩展开发依赖。", extensionDirectory, "ci");
AddNpmCommand(browser, "build", "构建扩展；随后在 Chrome 扩展页重新加载。", extensionDirectory, "run", "build");
var extensionId = new Argument<string>("extension-id") { Description = "Chrome 扩展页显示的扩展 ID。" };
var store = new Option<string>("--store")
{
    Description = "采集存储目录；相对路径基于当前工作目录。",
    DefaultValueFactory = _ => ".orisumi",
};
var register = new Command("register-chrome", "将扩展连接到已构建的 Orisumi.Cli。") { extensionId, store, configuration };
register.SetAction(result =>
{
    var executable = Path.Combine(Development.RepositoryRoot(), "src/Orisumi.Cli/bin",
        result.GetRequiredValue(configuration), "net11.0", "Orisumi");
    if (!File.Exists(executable))
        throw new IOException("未找到 Orisumi.Cli 构建产物，请先运行 Orisumi.Dev build。");
    var manifest = ChromeNativeHost.Register(result.GetRequiredValue(store), result.GetRequiredValue(extensionId), executable);
    AnsiConsole.Write(new Text($"Chrome 本机接收端已注册：{manifest}{Environment.NewLine}"));
});
browser.Add(register);

var root = new RootCommand("Orisumi · 开发工具") { build, docs, browser };
root.HelpName = "orisumi-dev";
try
{
    return await root.Parse(args).InvokeAsync(new InvocationConfiguration
    {
        EnableDefaultExceptionHandler = false,
        ProcessTerminationTimeout = TimeSpan.FromSeconds(10),
    });
}
catch (OperationCanceledException)
{
    return 130;
}
catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or Win32Exception)
{
    var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });
    console.Write(new Text($"orisumi-dev: {error.Message}{Environment.NewLine}", new Style(Color.Red)));
    return 1;
}

static void AddNpmCommand(Command parent, string name, string description, string directory, params string[] arguments)
{
    var command = new Command(name, description);
    command.SetAction((_, token) => Development.Run("npm", Path.Combine(Development.RepositoryRoot(), directory), token, arguments));
    parent.Add(command);
}
