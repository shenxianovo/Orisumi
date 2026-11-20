namespace Orisumi.Platform;

internal static class HostBundle
{
    internal const string Name = "Orisumi.app";
    internal const string AppExecutableName = "OrisumiApp";
    internal const string Identifier = "dev.orisumi";
    internal const string Authority = "Orisumi Development";
    internal const string Codesign = "/usr/bin/codesign";

    internal static string Executable(string release) =>
        Path.Combine(release, Name, "Contents", "MacOS", ProgramDescription.ExecutableName("host"));

    internal static string AppExecutable(string release) =>
        Path.Combine(release, Name, "Contents", "MacOS", AppExecutableName);

    internal static async Task Verify(string release, CancellationToken token)
    {
        var bundle = Path.Combine(release, Name);
        using var errors = new StringWriter();
        var code = await RunProcess.Run(Codesign, ["--verify", "--strict", bundle], TextWriter.Null, token, errors: errors);
        token.ThrowIfCancellationRequested();
        if (code != 0) throw new InvalidDataException($"{bundle}: strict signature verification failed: {errors.ToString().Trim()}");

        errors.GetStringBuilder().Clear();
        code = await RunProcess.Run(Codesign, ["--display", "--verbose=4", bundle], TextWriter.Null, token, errors: errors);
        token.ThrowIfCancellationRequested();
        var metadata = errors.ToString().Split('\n');
        if (code != 0 || !metadata.Contains($"Identifier={Identifier}") ||
            metadata.FirstOrDefault(line => line.StartsWith("Authority=", StringComparison.Ordinal)) != $"Authority={Authority}")
            throw new InvalidDataException($"{bundle}: expected identifier {Identifier} and signing authority {Authority}");
        var app = AppExecutable(release);
        if (!metadata.Any(line => line.StartsWith("Executable=", StringComparison.Ordinal) &&
                line.EndsWith($"/Contents/MacOS/{AppExecutableName}", StringComparison.Ordinal)) ||
            !File.Exists(app) || (File.GetUnixFileMode(app) & UnixFileMode.UserExecute) == 0)
            throw new InvalidDataException($"{bundle}: expected main executable {AppExecutableName}");
    }
}
