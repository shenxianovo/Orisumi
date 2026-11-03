using System.Text.Json;

namespace Orisumi.Dev;

internal sealed record ConfigKey(string Name, string Description, bool Secret);

internal sealed class UserConfig(string path)
{
    public static readonly ConfigKey OpenAIApiKey = new("openai.api-key", "OpenAI API key for model-judged review", Secret: true);

    public static readonly IReadOnlyList<ConfigKey> Keys = [OpenAIApiKey];

    public static UserConfig Default { get; } = new(System.IO.Path.Combine(ConfigHome(), "orisumi", "config.json"));

    public string Path { get; } = path;

    private static string ConfigHome() =>
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } home
            ? home
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

    public static ConfigKey Find(string name) =>
        Keys.FirstOrDefault(key => key.Name == name)
        ?? throw new CommandUsageException($"Unknown setting '{name}'. Settings: {string.Join(", ", Keys.Select(key => key.Name))}.");

    public string? Get(ConfigKey key) => Read().GetValueOrDefault(key.Name);

    public void Set(ConfigKey key, string? value)
    {
        var values = Read();
        if (value is null) values.Remove(key.Name);
        else values[key.Name] = value;
        Write(values);
    }

    public static string Display(ConfigKey key, string value) =>
        !key.Secret ? value : value.Length <= 12 ? "••••" : $"{value[..4]}…{value[^4..]}";

    private Dictionary<string, string> Read() =>
        File.Exists(Path)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path)) ?? []
            : [];

    private void Write(Dictionary<string, string> values)
    {
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var ordered = values.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary();
        var temporary = Path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(ordered, JsonOptions.Indented) + Environment.NewLine);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, Path, overwrite: true);
    }
}
