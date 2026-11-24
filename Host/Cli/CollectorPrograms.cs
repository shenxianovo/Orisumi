using System.CommandLine;
using Orisumi.Platform;
using Orisumi.Desktop;

namespace Orisumi.Cli;

internal sealed record CollectorProgram(string Path, bool Managed, ProgramDescription Description)
{
    internal string Executable => Managed ? RunProcess.Dotnet : Path;
    internal string[] Arguments(string[] arguments) => Managed ? [Path, .. arguments] : arguments;
    internal string[] Command(string store, string[] arguments) => [Executable, .. Arguments(["--store", store, .. arguments])];
    internal JobSpec Job(Store store) => new(Command(store.Root, Description.Arguments!), store.Root,
        System.IO.Path.Combine(store.Logs, Description.Name + ".out.log"),
        System.IO.Path.Combine(store.Logs, Description.Name + ".err.log"));
}

internal static class CollectorPrograms
{
    private static string? Checkout(string baseDirectory)
    {
        if (Instance.Find(baseDirectory) is not null) return null;
        for (var directory = new DirectoryInfo(baseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Orisumi.slnx")))
                return DirectoryPaths.Contains(Path.Combine(directory.FullName, "Host", "Cli"), baseDirectory) ? directory.FullName : null;
        return null;
    }

    internal static async Task<CollectorProgram[]> Read(string store, CancellationToken token, string? baseDirectory = null)
    {
        baseDirectory ??= AppContext.BaseDirectory;
        var checkout = Checkout(baseDirectory);
        var managed = checkout is not null;
        var directory = managed ? Path.Combine(checkout!, "Collectors") : new Store(store).Collectors;
        if (!Directory.Exists(directory)) return [];
        var output = managed ? Path.GetRelativePath(Path.Combine(checkout!, "Host", "Cli"), baseDirectory) : "";
        var rid = Platforms.CurrentRid;
        var files = Directory.EnumerateDirectories(directory)
            .Where(project => managed || Instance.IsName(Path.GetFileName(project)))
            .Where(project => Platforms.Runs(Path.GetFileName(project).ToLowerInvariant(), rid))
            .Select(project => Path.Combine(project, output))
            .Where(Directory.Exists)
            .SelectMany(project => managed ? Directory.EnumerateFiles(project, "orisumi-*.dll")
                : [Path.Combine(project, ProgramDescription.ExecutableName(Path.GetFileName(project), rid))])
            .Where(File.Exists);
        return await Task.WhenAll(files.Order(StringComparer.Ordinal).Select(Describe));

        async Task<CollectorProgram> Describe(string path)
        {
            var description = await ProgramDescription.Read(managed ? RunProcess.Dotnet : path, managed ? [path] : [], token);
            if (description.Arguments is null || (!managed && description.Name != Path.GetFileName(Path.GetDirectoryName(path))))
                throw new InvalidDataException($"{path}: invalid collector name {description.Name}");
            return new(path, managed, description);
        }
    }

    internal static Task<int?> TryForward(RootCommand root, string[] arguments)
    {
        var index = 0;
        string? store = null;
        // Only inspect the host prefix. The selected collector parses the remaining tokens.
        while (index < arguments.Length)
        {
            var option = arguments[index];
            if (option == "--store" && index + 1 < arguments.Length) store = arguments[++index];
            else if (InlineStore(option)) store = option[8..];
            else break;
            index++;
        }
        if (index == arguments.Length || arguments[index].StartsWith('-') ||
            root.Subcommands.Any(command => command.Name == arguments[index]))
            return Task.FromResult<int?>(null);

        return RunProcess.WithCancellation<int?>(async token =>
        {
            var remaining = arguments[(index + 1)..];
            for (var i = 0; i < remaining.Length && remaining[i] != "--"; i++)
            {
                if (remaining[i] == "--store" && i + 1 < remaining.Length) store = remaining[++i];
                else if (InlineStore(remaining[i])) store = remaining[i][8..];
            }
            var location = store ?? Instance.DefaultRoot();
            var programs = await Read(location, token);
            var program = programs.SingleOrDefault(program => program.Description.Name == arguments[index]);
            if (program is null) return null;
            var forwarded = store is not null ? arguments[..index].Concat(remaining).ToArray() : ["--store", location, .. remaining];
            return await RunProcess.Run(program.Executable, program.Arguments(forwarded), output: null, token, preserveExitCode: true);
        });
    }

    private static bool InlineStore(string option) =>
        option.StartsWith("--store=", StringComparison.Ordinal) || option.StartsWith("--store:", StringComparison.Ordinal);
}
