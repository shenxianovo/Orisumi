using Orisumi.Desktop;

sealed class MemoryJobs : ICollectorJobs
{
    internal sealed record Registration(string Name, JobSpec? Spec)
        : SavedJob(Name, Spec is not null, Spec is not null, Spec is null ? "not_loaded" : "waiting", null, null);

    internal Dictionary<string, Registration> Jobs { get; } = new();
    internal List<string> Changes { get; } = [];
    internal string? Fail { get; set; }

    public Task<IReadOnlyDictionary<string, SavedJob>> Inspect(IEnumerable<string> sources, CancellationToken token) =>
        Task.FromResult<IReadOnlyDictionary<string, SavedJob>>(sources.Union(Jobs.Keys)
            .ToDictionary(name => name, name => (SavedJob)(Jobs.GetValueOrDefault(name) ?? new Registration(name, null))));

    public Task<string> Set(string source, JobSpec? desired, CancellationToken token)
    {
        Changes.Add("set " + source);
        Jobs[source] = new Registration(source, desired);
        if (source == Fail) throw new IOException(source);
        return Task.FromResult(source);
    }

    public Task Restore(SavedJob previous, CancellationToken token)
    {
        Changes.Add("restore " + previous.Source);
        Jobs[previous.Source] = (Registration)previous;
        return Task.CompletedTask;
    }
}
