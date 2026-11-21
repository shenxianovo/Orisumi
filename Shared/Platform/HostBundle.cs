namespace Orisumi.Platform;

internal static class HostBundle
{
    internal const string Name = "Orisumi.app";
    internal const string AppExecutableName = "OrisumiApp";
    internal const string Authority = "Orisumi Development";
    internal const string ReleaseCertificateSha256 = "ABE1CF8B06D225BA737B8E1C84F8923A9705526964CF9B48AF1A47ED8CA552BE";
    internal sealed record Tools(string Codesign, CommandProcess Run)
    {
        internal static Tools Current { get; } = new("/usr/bin/codesign", RunProcess.RunCommand);
    }

    internal static string Executable(string release) =>
        Path.Combine(release, Name, "Contents", "MacOS", ProgramDescription.ExecutableName("host"));

    internal static string AppExecutable(string release) =>
        Path.Combine(release, Name, "Contents", "MacOS", AppExecutableName);

    internal static async Task Verify(string release, string instance, CancellationToken token, Tools tools)
    {
        var bundle = Path.Combine(release, Name);
        using var errors = new StringWriter();
        var code = await RunProcess.Run(tools.Codesign, ["--verify", "--strict", bundle], TextWriter.Null, token, errors: errors);
        token.ThrowIfCancellationRequested();
        if (code != 0) throw new InvalidDataException($"{bundle}: strict signature verification failed: {errors.ToString().Trim()}");

        errors.GetStringBuilder().Clear();
        code = await RunProcess.Run(tools.Codesign, ["--display", "--verbose=4", bundle], TextWriter.Null, token, errors: errors);
        token.ThrowIfCancellationRequested();
        var metadata = errors.ToString().Split('\n');
        var identifier = Instance.BundleIdentifierFor(instance);
        if (code != 0 || !metadata.Contains($"Identifier={identifier}") ||
            metadata.FirstOrDefault(line => line.StartsWith("Authority=", StringComparison.Ordinal)) != $"Authority={Authority}")
            throw new InvalidDataException($"{bundle}: expected identifier {identifier} and signing authority {Authority}");
        var app = AppExecutable(release);
        if (!metadata.Any(line => line.StartsWith("Executable=", StringComparison.Ordinal) &&
                line.EndsWith($"/Contents/MacOS/{AppExecutableName}", StringComparison.Ordinal)) ||
            !File.Exists(app) || (File.GetUnixFileMode(app) & UnixFileMode.UserExecute) == 0)
            throw new InvalidDataException($"{bundle}: expected main executable {AppExecutableName}");
    }

    internal static async Task VerifyCertificate(string release, string instance, string expected,
        CancellationToken token, Tools tools)
    {
        await Verify(release, instance, token, tools);
        var bundle = Path.Combine(release, Name);
        var temporary = Directory.CreateTempSubdirectory("orisumi-certificate-");
        try
        {
            using var errors = new StringWriter();
            var prefix = Path.Combine(temporary.FullName, "certificate-");
            var code = await RunProcess.Run(tools.Codesign, ["--display", $"--extract-certificates={prefix}", bundle],
                TextWriter.Null, token, errors: errors);
            token.ThrowIfCancellationRequested();
            var certificate = prefix + "0";
            if (code != 0 || !File.Exists(certificate))
                throw new InvalidDataException($"{bundle}: 无法读取签名证书：{errors.ToString().Trim()}");
            var actual = ReleaseManifest.HashFile(certificate);
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{bundle}: 签名证书 SHA-256 不匹配，预期 {expected}，实际 {actual}。");
        }
        finally { temporary.Delete(recursive: true); }
    }
}
