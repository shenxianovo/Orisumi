using Orisumi.Desktop;
using Orisumi.Core;

namespace Orisumi.Cli;

internal sealed record DesktopServices(Func<Instance, ICollectorJobs> Jobs, Func<Instance, IDesktopInstallation> Installation);
