namespace Orisumi.Desktop;

internal sealed record ScheduledTaskState(string Xml, bool Active);

internal interface IWindowsScheduledTask
{
    string Name { get; }
    ScheduledTaskState? Query(CancellationToken token);
    void Register(string? xml, CancellationToken token);
    void Start(CancellationToken token);
    void End(CancellationToken token);
    void Delete(CancellationToken token);
}

internal sealed class WindowsLoginRegistration(IWindowsScheduledTask task, CancellationToken prepareToken) : ILoginRegistration
{
    private readonly ScheduledTaskState? previous = task.Query(prepareToken);

    public Task Apply(CancellationToken token)
    {
        task.Register(null, token);
        if (task.Query(token)?.Active != true) task.Start(token);
        return Task.CompletedTask;
    }

    public Task Restore(CancellationToken token)
    {
        if (previous?.Active != true && task.Query(token)?.Active == true) task.End(token);
        if (previous is null) task.Delete(token);
        else
        {
            task.Register(previous.Xml, token);
            if (previous.Active && task.Query(token)?.Active != true) task.Start(token);
        }
        return Task.CompletedTask;
    }
}
