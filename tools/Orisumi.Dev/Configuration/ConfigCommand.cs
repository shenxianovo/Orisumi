using System.CommandLine;
using System.CommandLine.Help;
using System.Text.Json;
using Spectre.Console;

namespace Orisumi.Dev;

internal sealed class ConfigCommand(RepositoryContext repository, UserConfig config, TextWriter output, TextReader input)
{
    public Command CreateCommand()
    {
        var command = new Command("config", "Show or change user settings shared by every checkout on this machine");
        command.SetAction(parse => new HelpAction().Invoke(parse));

        var list = new Command("list", "List settings; secrets are masked");
        var json = new Option<bool>("--json") { Description = "Print JSON" };
        list.Options.Add(json);
        list.SetAction(parse => ListAsync(parse.GetValue(json)));

        var set = new Command("set", "Set a value; without one, read it from a hidden prompt or stdin");
        var setName = NameArgument();
        var value = new Argument<string?>("value") { Arity = ArgumentArity.ZeroOrOne, Description = "Value; omit it to keep secrets out of shell history" };
        set.Arguments.Add(setName);
        set.Arguments.Add(value);
        set.SetAction(parse => Set(UserConfig.Find(parse.GetValue(setName)!), parse.GetValue(value)));

        var unset = new Command("unset", "Remove a value");
        var unsetName = NameArgument();
        unset.Arguments.Add(unsetName);
        unset.SetAction(parse => Unset(UserConfig.Find(parse.GetValue(unsetName)!)));

        command.Subcommands.Add(list);
        command.Subcommands.Add(set);
        command.Subcommands.Add(unset);
        return command;
    }

    private static Argument<string> NameArgument() =>
        new("name") { Description = $"Setting: {string.Join(", ", UserConfig.Keys.Select(key => key.Name))}" };

    private async Task<int> ListAsync(bool json)
    {
        var settings = UserConfig.Keys.Select(key => (Key: key, Value: config.Get(key))).ToArray();
        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                path = config.Path,
                settings = settings.Select(setting => new
                {
                    name = setting.Key.Name,
                    set = setting.Value is not null,
                    value = setting.Value is { } value ? UserConfig.Display(setting.Key, value) : null,
                    description = setting.Key.Description,
                }),
            }, JsonOptions.Indented));
            return 0;
        }
        var console = Terminal.Create(output);
        var count = settings.Count(setting => setting.Value is not null);
        Terminal.Heading(console, "Config", Terminal.Esc(Terminal.Path(repository, config.Path)), $"{count} of {settings.Length} set");
        Terminal.Rows(console, settings.Select(setting => new[]
        {
            Terminal.Esc(setting.Key.Name),
            setting.Value is { } value ? Terminal.Esc(UserConfig.Display(setting.Key, value)) : "[grey]not set[/]",
            $"[grey]{Terminal.Esc(setting.Key.Description)}[/]",
        }));
        return 0;
    }

    private int Set(ConfigKey key, string? value)
    {
        var console = Terminal.Create(output);
        value = (value ?? Prompt(console, key)).Trim();
        if (value.Length == 0) throw new CommandUsageException($"No value for {key.Name}.");
        config.Set(key, value);
        console.MarkupLine($"{Terminal.Mark(Outcome.Passed)} Set {Terminal.Esc(key.Name)} "
            + $"[grey]{Terminal.Esc(UserConfig.Display(key, value))} in {Terminal.Esc(Terminal.Path(repository, config.Path))}[/]");
        return 0;
    }

    private string Prompt(IAnsiConsole console, ConfigKey key)
    {
        if (!ReferenceEquals(input, Console.In) || Console.IsInputRedirected) return input.ReadToEnd();
        var prompt = new TextPrompt<string>(key.Name);
        return console.Prompt(key.Secret ? prompt.Secret() : prompt);
    }

    private int Unset(ConfigKey key)
    {
        config.Set(key, null);
        Terminal.Create(output).MarkupLine($"{Terminal.Mark(Outcome.Passed)} Unset {Terminal.Esc(key.Name)}");
        return 0;
    }
}
