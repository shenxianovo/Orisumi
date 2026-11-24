using System.CommandLine;
using System.Reflection;
using Orisumi.Platform;

namespace Orisumi.Cli;

internal static partial class Commands
{
    internal static string HostVersion { get; } =
        typeof(Commands).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

    private static partial DesktopServices? CurrentDesktop();
    internal static partial Task<int?> Forward(RootCommand root, string[] arguments);

    internal static RootCommand Create(TextWriter? output = null, string? baseDirectory = null) =>
        Create(CurrentDesktop(), output ?? Console.Out, baseDirectory);

    internal static RootCommand Create(DesktopServices? desktop, TextWriter output, string? baseDirectory = null)
    {
        var store = new Option<string?>("--store")
        {
            Description = "采集存储目录，默认使用程序所属的实例。",
            Recursive = true,
        };
        var root = new RootCommand("Orisumi · 采集与查询") { store };
        root.HelpName = "orisumi";

        root.Add(Capture.CaptureCommand.Create(store, desktop, output, baseDirectory));
        root.Add(Day.DayCommand.Create(store, output));

        var destination = new Argument<string>("destination") { Description = "观测库快照的目标目录。" };
        var export = new Command("export", "导出所有观测库的 SQLite 快照。") { destination };
        export.SetAction(result => StoreExport.Write(result.GetValue(store) ?? Instance.DefaultRoot(), result.GetRequiredValue(destination), output));
        root.Add(export);

        var describe = new Command("describe", "输出宿主版本。");
        describe.SetAction(_ => new ProgramDescription("host", HostVersion).Write(output));
        root.Add(describe);

        if (desktop is null) return root;

        var release = new Argument<string>("release-dir");
        var install = new Command("install", "将发布目录安装到实例。") { release };
        install.SetAction((result, token) =>
        {
            var instance = Instance.Open(result.GetValue(store) ?? Instance.DefaultRoot());
            return InstallCommand.Run(instance, result.GetRequiredValue(release), desktop.Jobs(instance),
                desktop.Installation(instance), output, token);
        });
        root.Add(install);
        root.Add(UpdateCommand.Create(store, desktop, output, baseDirectory));
        return root;
    }
}
