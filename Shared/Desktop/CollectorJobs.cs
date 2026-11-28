using System.Text.Json.Nodes;

namespace Orisumi.Desktop;

internal sealed record JobSpec(string[] Command, string WorkingDirectory, string StandardOutput, string StandardError, string? Version = null);

internal record SavedJob(string Source, bool Registered, bool Supervised, string State, int? Pid, JsonValue? LastExitCode);

// Inspect includes the requested sources, installed collectors and registrations belonging to this instance.
internal interface ICollectorJobs
{
    Task<IReadOnlyDictionary<string, SavedJob>> Inspect(IEnumerable<string> sources, CancellationToken token);
    Task<string> Set(string source, JobSpec? desired, CancellationToken token);
    Task Restore(SavedJob previous, CancellationToken token);
}

internal static class CollectorJobs
{
    internal static async Task StopAll(this ICollectorJobs jobs, CancellationToken token)
    {
        var known = await jobs.Inspect([], token);
        await Task.WhenAll(known.Values.Where(job => job.Supervised || job.Registered)
            .Select(job => jobs.Set(job.Source, null, token)));
    }
}
