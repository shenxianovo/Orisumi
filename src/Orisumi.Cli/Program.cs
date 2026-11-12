using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Orisumi.Collectors.Browser;
using Orisumi.Collectors.MacOS;

const string usage = """
    Usage: orisumi [--store DIRECTORY] <command>

      foreground [SECONDS] [--windows] [--afk-threshold SECONDS] [--input]
      timeline --from TIMESTAMP --to TIMESTAMP
      browser register-chrome EXTENSION_ID

    Raw databases are stored in DIRECTORY/raw, with .orisumi as the default DIRECTORY.
    """;

try
{
    var (store, command, commandArgs) = ParseArguments(args);
    if (command is "-h" or "--help" || commandArgs.TakeWhile(arg => arg != "--").Any(arg => arg is "-h" or "--help"))
    {
        Console.WriteLine(usage);
        return 0;
    }

    switch (command)
    {
        case "foreground":
            return await CaptureForeground(store, commandArgs);
        case "timeline":
            var (from, to) = ParseTimelineRange(commandArgs);
            await ActivityTimeline.Write(store, from, to, Console.Out);
            break;
        case "browser":
            if (commandArgs is ["register-chrome", var extensionId])
            {
                var executable = Path.Combine(AppContext.BaseDirectory, "Orisumi");
                if (!File.Exists(executable))
                    throw new IOException("could not find the Orisumi executable for Chrome");
                Console.WriteLine(ChromeNativeHost.Register(store, extensionId, executable));
            }
            else if (commandArgs is ["native-host"])
            {
                await ChromeNativeHost.Run(store);
            }
            else
            {
                throw new ArgumentException("browser expects register-chrome EXTENSION_ID");
            }
            break;
        default:
            throw new ArgumentException($"unknown command: {command}");
    }

    return 0;
}
catch (ArgumentException error)
{
    Console.Error.WriteLine($"orisumi: {error.Message}");
    Console.Error.WriteLine(usage);
    return 2;
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or Win32Exception)
{
    Console.Error.WriteLine($"orisumi: {error.Message}");
    return 1;
}

static Task<int> CaptureForeground(string store, string[] arguments)
{
    var positionals = new List<string>();
    var captureWindows = false;
    var captureInput = false;
    int? afkThresholdSeconds = null;
    for (var index = 0; index < arguments.Length; index++)
    {
        var argument = arguments[index];
        if (argument == "--")
        {
            positionals.AddRange(arguments[(index + 1)..]);
            break;
        }

        if (argument == "--windows")
        {
            if (captureWindows)
                throw new ArgumentException("duplicate foreground option: --windows");
            captureWindows = true;
        }
        else if (argument == "--input")
        {
            if (captureInput)
                throw new ArgumentException("duplicate foreground option: --input");
            captureInput = true;
        }
        else if (argument == "--afk-threshold" || argument.StartsWith("--afk-threshold=", StringComparison.Ordinal))
        {
            if (afkThresholdSeconds is not null)
                throw new ArgumentException("duplicate foreground option: --afk-threshold");
            var value = argument == "--afk-threshold"
                ? OptionValue(arguments, ref index)
                : argument["--afk-threshold=".Length..];
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var threshold) || threshold <= 0)
                throw new ArgumentException("--afk-threshold expects a positive number of seconds");
            afkThresholdSeconds = threshold;
        }
        else if (argument.StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"unknown foreground option: {argument}");
        }
        else
        {
            positionals.Add(argument);
        }
    }

    if (positionals.Count > 1)
        throw new ArgumentException("foreground expects an optional positive number of seconds");

    int? seconds = null;
    if (positionals.Count == 1)
    {
        if (!int.TryParse(positionals[0], NumberStyles.None, CultureInfo.InvariantCulture, out var duration) || duration <= 0)
            throw new ArgumentException("foreground expects an optional positive number of seconds");
        seconds = duration;
    }

    return MacOSForeground.Run(store, seconds, captureWindows, afkThresholdSeconds, captureInput);
}

static (DateTimeOffset From, DateTimeOffset To) ParseTimelineRange(string[] arguments)
{
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index < arguments.Length; index++)
    {
        var option = arguments[index].Split('=', 2);
        var name = option[0];
        if (name is not ("--from" or "--to"))
            throw new ArgumentException($"unknown timeline option: {name}");
        var value = option.Length == 2 ? option[1] : OptionValue(arguments, ref index);
        if (!options.TryAdd(name, value))
            throw new ArgumentException($"duplicate timeline option: {name}");
    }

    var from = ParseTimelineTimestamp(RequiredOption(options, "--from"), "--from");
    var to = ParseTimelineTimestamp(RequiredOption(options, "--to"), "--to");
    if (from >= to)
        throw new ArgumentException("--from must be earlier than --to");
    return (from, to);
}

static DateTimeOffset ParseTimelineTimestamp(string value, string option)
{
    if (!Regex.IsMatch(value, @"(?:Z|[+-]\d{2}:\d{2})\z") ||
        !DateTimeOffset.TryParseExact(value, ["yyyy-MM-dd'T'HH:mmK", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
        throw new ArgumentException($"{option} must be an ISO 8601 timestamp with Z or a UTC offset");
    return timestamp;
}

static (string Store, string Command, string[] Arguments) ParseArguments(string[] args)
{
    var store = ".orisumi";
    var index = 0;
    for (; index < args.Length; index++)
    {
        if (args[index] == "--store")
            store = OptionValue(args, ref index);
        else if (args[index].StartsWith("--store=", StringComparison.Ordinal))
            store = args[index]["--store=".Length..];
        else
            return (store, args[index], args[(index + 1)..]);
    }

    throw new ArgumentException("a command is required");
}

static string OptionValue(string[] arguments, ref int index)
{
    var option = arguments[index++];
    if (index == arguments.Length || arguments[index].StartsWith("--", StringComparison.Ordinal))
        throw new ArgumentException($"expected a value for {option}");
    return arguments[index];
}

static string RequiredOption(Dictionary<string, string> options, string name) =>
    options.TryGetValue(name, out var value) ? value : throw new ArgumentException($"{name} is required");
