using Orisumi.Desktop;
using Orisumi.Platform;

namespace Orisumi.Cli;

internal sealed record DesktopServices(Func<Instance, ICollectorJobs> Jobs, Func<Instance, IDesktopInstallation> Installation);
