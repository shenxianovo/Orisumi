using System.Runtime.InteropServices;

namespace Orisumi.Collectors.Hosting;

internal sealed class CaptureLifetime : IDisposable
{
    private readonly CancellationTokenSource stop;
    private readonly PosixSignalRegistration termination;

    internal CaptureLifetime(CancellationToken cancellation = default)
    {
        stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        termination = PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal =>
        {
            signal.Cancel = true;
            stop.Cancel();
        });
    }

    internal CancellationToken Token => stop.Token;

    internal static async Task Run(Func<CancellationToken, Task> watch, CancellationToken cancellation)
    {
        using var lifetime = new CaptureLifetime(cancellation);
        try { await watch(lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.Token.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        termination.Dispose();
        stop.Dispose();
    }
}
