using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orisumi.Platform;

public sealed record ProgramDescription(string Name, string Version,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? Arguments = null)
{
    public static async Task<ProgramDescription> Read(string executable, string[] prefix, CancellationToken token)
    {
        using var output = new StringWriter();
        var code = await RunProcess.Run(executable, [.. prefix, "describe"], output, token);
        token.ThrowIfCancellationRequested();
        if (code != 0) throw new IOException($"{executable} {string.Join(' ', prefix)}: describe exited with code {code}");
        var description = JsonSerializer.Deserialize(output.ToString(), DescriptionJson.Default.ProgramDescription);
        if (description is not { Name: { } name, Version.Length: > 0 } || !Instance.IsName(name) ||
            (name == "host" ? description.Arguments is not null :
                description.Arguments is not [{ Length: > 0 }, ..] arguments || arguments.Any(argument => argument is null)))
            throw new InvalidDataException($"{executable} {string.Join(' ', prefix)}: invalid program description");
        return description;
    }

    public void Write(TextWriter output) => output.WriteLine(JsonSerializer.Serialize(this, DescriptionJson.Default.ProgramDescription));

    public static string ExecutableName(string component) => component == "host" ? "orisumi" : "orisumi-" + component;
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ProgramDescription))]
internal sealed partial class DescriptionJson : JsonSerializerContext;
