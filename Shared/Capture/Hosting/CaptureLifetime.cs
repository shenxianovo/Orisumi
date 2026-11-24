using Orisumi.Platform;

namespace Orisumi.Capture.Hosting;

public sealed class CaptureLifetime : IDisposable
{
    private readonly CancellationTokenSource stop;
    private readonly IDisposable termination;

    public CaptureLifetime(CancellationToken cancellation = default)
    {
        stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        termination = RunProcess.RegisterTermination(stop);
    }

    public CancellationToken Token => stop.Token;

    public static async Task Run(Func<CancellationToken, Task> watch, CancellationToken cancellation)
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
