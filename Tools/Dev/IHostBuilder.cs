using Orisumi.Platform;

namespace Orisumi.Dev;

internal interface IHostBuilder
{
    CommandProcess Run { get; }

    Task Complete(string checkout, string directory, string instance, string projectVersion, string version, CancellationToken token);
}
