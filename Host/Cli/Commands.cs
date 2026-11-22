using System.CommandLine;
using System.Reflection;
using Orisumi.Platform;

namespace Orisumi.Cli;

internal static class Commands
{
    internal static string HostVersion { get; } =
        typeof(Commands).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

    internal static RootCommand Create(SystemLocations? locations = null, TextWriter? output = null, string? baseDirectory = null)
    {
        locations ??= SystemLocations.Current;
        output ??= Console.Out;
        var store = new Option<string?>("--store")
        {
            Description = "采集存储目录，默认使用程序所属的实例。",
            Recursive = true,
        };
        var root = new RootCommand("Orisumi · 采集与查询") { store };
        root.HelpName = "orisumi";

        root.Add(Capture.CaptureCommand.Create(store, locations, output, baseDirectory));
        root.Add(Day.DayCommand.Create(store, output));

        var destination = new Argument<string>("destination") { Description = "观测库快照的目标目录。" };
        var export = new Command("export", "导出所有观测库的 SQLite 快照。") { destination };
        export.SetAction(result => StoreExport.Write(result.GetValue(store) ?? Instance.DefaultRoot(), result.GetRequiredValue(destination), output));
        root.Add(export);

        var describe = new Command("describe", "输出宿主版本。");
        describe.SetAction(_ => new ProgramDescription("host", HostVersion).Write(output));
        root.Add(describe);

        var release = new Argument<string>("release-dir");
        var install = new Command("install", "将发布目录安装到实例。") { release };
        install.SetAction((result, token) => InstallCommand.Run(Instance.Open(result.GetValue(store) ?? Instance.DefaultRoot()),
            result.GetRequiredValue(release), locations, output, token));
        root.Add(install);
        root.Add(UpdateCommand.Create(store, locations, output, baseDirectory));
        return root;
    }
}
