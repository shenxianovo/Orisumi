using Orisumi.Platform;

namespace Orisumi.Desktop;

internal sealed class WindowsInstallation(Instance instance) : IDesktopInstallation, ILoginRegistration
{
    private string Retired => Path.Combine(instance.Store.App, ".retired");
    public string HostExecutable(string directory) => Store.HostExecutableIn(directory);
    public CommandProcess RunHost => RunProcess.RunCommand;

    public async Task VerifyHost(string directory, bool requireReleaseCertificate, CancellationToken token)
    {
        var supervisor = Path.Combine(directory, WindowsSupervisor.Executable);
        if (!File.Exists(supervisor)) throw new FileNotFoundException($"缺少托管程序：{supervisor}");
        if ((await ProgramDescription.Read(HostExecutable(directory), [], token)).Name != "host")
            throw new InvalidDataException($"无效的宿主：{directory}");
    }

    public async Task ExtractPackage(string archive, string directory, CancellationToken token)
    {
        using var errors = new StringWriter();
        var exit = await RunProcess.Run(Path.Combine(Environment.SystemDirectory, "tar.exe"),
            ["-xf", archive, "-C", directory], TextWriter.Null, token, errors: errors);
        token.ThrowIfCancellationRequested();
        if (exit != 0) throw new InvalidDataException($"{archive}: {errors}");
    }

    public void CleanRetired()
    {
        if (!Directory.Exists(Retired)) return;
        foreach (var path in Directory.EnumerateDirectories(Retired))
        {
            try { Directory.Delete(path, recursive: true); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 or 145) { }
            catch (UnauthorizedAccessException) when (Directory.Exists(path)) { } // Windows maps deletion of a mapped image to access denied.
        }
    }

    public Action ActivateDirectory(string staged, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (!Directory.Exists(destination))
        {
            Directory.Move(staged, destination);
            return () => Directory.Move(destination, staged);
        }
        Directory.CreateDirectory(Retired);
        var previous = Path.Combine(Retired, Guid.NewGuid().ToString("N"));
        Directory.Move(destination, previous);
        try { Directory.Move(staged, destination); }
        catch { Directory.Move(previous, destination); throw; }
        return () =>
        {
            // The replacement can already be executing when a later component fails.
            Directory.Move(destination, Path.Combine(Retired, Guid.NewGuid().ToString("N")));
            Directory.Move(previous, destination);
        };
    }

    public Task<ILoginRegistration> PrepareLogin(CancellationToken token) => Task.FromResult<ILoginRegistration>(this);
    public Task Apply(CancellationToken token) => Task.CompletedTask;
    public Task Restore(CancellationToken token) => Task.CompletedTask;
    public void LinkCommand() { }
}
