using System.CommandLine;
using System.CommandLine.Invocation;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orisumi.Platform;

namespace Orisumi.Collectors.Hosting;

internal static class CollectorCommand
{
    internal static string Version { get; } =
        typeof(CollectorCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

    internal static Option<string?> StoreOption() => new("--store")
    {
        Description = "采集存储目录，默认使用程序所属的实例。",
        Recursive = true,
    };

    internal static RootCommand Create(string name, string description, Option<string?> store, string[] arguments)
    {
        var describe = new Command("describe", "输出采集器版本与启动参数。");
        describe.SetAction(_ => new ProgramDescription(name, Version, arguments).Write(Console.Out));
        var root = new RootCommand(description) { store, describe };
        root.HelpName = "orisumi-" + name;
        return root;
    }

    internal static async Task<int> Run(RootCommand root, string[] arguments)
    {
        try
        {
            var parsed = root.Parse(arguments);
            if (parsed.Action is ParseErrorAction)
            {
                foreach (var error in parsed.Errors)
                    Console.Error.WriteLine(error.Message);
                return 2;
            }
            return await parsed.InvokeAsync(new InvocationConfiguration
            {
                EnableDefaultExceptionHandler = false,
                ProcessTerminationTimeout = TimeSpan.FromSeconds(10),
            });
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or
            InvalidDataException or JsonException or Win32Exception or SqliteException)
        {
            Console.Error.WriteLine($"{root.HelpName}: {error.Message}");
            return error is ArgumentException ? 2 : 1;
        }
    }
}
