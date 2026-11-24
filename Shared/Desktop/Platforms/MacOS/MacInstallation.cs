using Orisumi.Platform;

namespace Orisumi.Desktop;

internal sealed class MacInstallation(Instance instance, SystemLocations locations, HostBundle.Tools host,
    string certificate = HostBundle.ReleaseCertificateSha256) : IDesktopInstallation
{
    public string HostExecutable(string directory) => HostBundle.Executable(directory);
    public CommandProcess RunHost => host.Run;
    public Task VerifyHost(string directory, bool requireReleaseCertificate, CancellationToken token) =>
        requireReleaseCertificate ? HostBundle.VerifyCertificate(directory, instance.Name, certificate, token, host)
            : HostBundle.Verify(directory, instance.Name, token, host);

    public async Task ExtractPackage(string archive, string directory, CancellationToken token)
    {
        using var errors = new StringWriter();
        var exit = await RunProcess.Run("/usr/bin/tar", ["-xf", archive, "-C", directory], TextWriter.Null, token, errors: errors);
        token.ThrowIfCancellationRequested();
        if (exit != 0) throw new InvalidDataException($"{Path.GetFileName(archive)}: 无法解包：{errors.ToString().Trim()}");
    }

    public Action ActivateDirectory(string staged, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (Directory.Exists(destination))
        {
            DirectoryExchange.Exchange(staged, destination);
            return () => DirectoryExchange.Exchange(staged, destination);
        }
        Directory.Move(staged, destination);
        return () => Directory.Move(destination, staged);
    }

    public async Task<ILoginRegistration> PrepareLogin(CancellationToken token)
    {
        var path = Path.Combine(locations.Agents, instance.AppAgentLabel + ".plist");
        var loaded = await Launchd.Print(locations, Launchd.Service(instance.AppAgentLabel), token) is not null;
        return new LoginRegistration(instance, locations, path, File.Exists(path) ? File.ReadAllBytes(path) : null, loaded);
    }

    private sealed record LoginRegistration(Instance Instance, SystemLocations Locations, string Path, byte[]? Registration, bool Loaded)
        : ILoginRegistration
    {
        public Task Apply(CancellationToken token) => AppStartup.Install(Instance, Locations, Loaded, token);
        public Task Restore(CancellationToken token) => Launchd.Restore(Path, Locations, Registration, Loaded, token);
    }

    public void LinkCommand()
    {
        if (instance.CommandName is not { } name) return;
        Directory.CreateDirectory(locations.Commands);
        var path = Path.Combine(locations.Commands, name);
        if (new FileInfo(path).LinkTarget == instance.Store.HostExecutable) return;
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.CreateSymbolicLink(temporary, instance.Store.HostExecutable);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
