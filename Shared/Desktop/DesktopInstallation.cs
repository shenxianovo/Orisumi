using Orisumi.Platform;

namespace Orisumi.Desktop;

// Prepare the login registration before touching it so a partially applied change can be undone.
internal interface ILoginRegistration
{
    Task Apply(CancellationToken token);
    Task Restore(CancellationToken token);
}

internal interface IDesktopInstallation
{
    void CleanRetired() { }
    string HostExecutable(string directory);
    CommandProcess RunHost { get; }
    Task VerifyHost(string directory, bool requireReleaseCertificate, CancellationToken token);
    Task ExtractPackage(string archive, string directory, CancellationToken token);
    Action ActivateDirectory(string staged, string destination);
    Task<ILoginRegistration> PrepareLogin(CancellationToken token);
    void LinkCommand();
}
