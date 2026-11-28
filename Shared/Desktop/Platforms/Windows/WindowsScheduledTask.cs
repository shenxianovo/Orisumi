using System.Runtime.InteropServices;
using System.Security.Principal;
using Orisumi.Core;

namespace Orisumi.Desktop;

internal sealed partial class WindowsScheduledTask(Instance instance, string taskName, Func<string, string> definition)
    : IWindowsScheduledTask
{
    internal static WindowsScheduledTask Capture(Instance instance) => new(instance, "Capture", sid =>
        WindowsTaskDefinition.Capture(sid, Path.Combine(instance.Store.Host, WindowsInstallation.SupervisorExecutable),
            instance.Store.Root).ToString());

    internal static WindowsScheduledTask App(Instance instance) => new(instance, "App", sid =>
        WindowsTaskDefinition.App(sid, Path.Combine(instance.Store.Host, WindowsInstallation.AppExecutable)).ToString());

    private const string Parent = @"\Orisumi";
    private string Folder => Parent + @"\" + instance.Name;
    public string Name => Folder + @"\" + taskName;
    private const int CreateOrUpdate = 6, InteractiveToken = 3, IncludeHidden = 1;
    private const int AlreadyExists = unchecked((int)0x800700b7);

    internal static bool Found(int hr)
    {
        if (hr is unchecked((int)0x80070002) or unchecked((int)0x80070003)) return false;
        Marshal.ThrowExceptionForHR(hr, -1);
        return true;
    }

    public ScheduledTaskState? Query(CancellationToken token)
    {
        using var session = new SchedulerSession(token);
        if (!Found(session.Service.GetFolder(Folder, out var folder)) || !Found(folder!.GetTask(taskName, out var task)))
            return null;
        task!.GetState(out var state);
        const int queued = 2, running = 4;
        return new(task.GetXml(), state is queued or running);
    }

    public void Register(string? xml, CancellationToken token)
    {
        using var session = new SchedulerSession(token);
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!.Value;
        // An SSH install can run with the user's elevated token. Grant the same user's
        // limited token control of this instance folder and its task.
        var descriptor = Marshal.StringToBSTR($"D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;{sid})");
        try
        {
            var security = new TaskVariant(descriptor);
            var service = session.Service;
            if (!Found(service.GetFolder(Folder, out var folder)))
            {
                if (!Found(service.GetFolder(Parent, out var parent)))
                    parent = CreateFolder(RequireFolder(service, @"\"), "Orisumi", default);
                folder = CreateFolder(parent!, instance.Name, security);
            }
            folder!.RegisterTask(taskName, xml ?? definition(sid), CreateOrUpdate, default, default, InteractiveToken, security);
        }
        finally { Marshal.FreeBSTR(descriptor); }
    }

    public void Start(CancellationToken token)
    {
        using var session = new SchedulerSession(token);
        RequireTask(session.Service).Run(default, out var running);
        if (running != 0) Marshal.Release(running);
    }

    public void End(CancellationToken token)
    {
        using var session = new SchedulerSession(token);
        RequireTask(session.Service).Stop(0);
    }

    public void Delete(CancellationToken token)
    {
        using var session = new SchedulerSession(token);
        if (!Found(session.Service.GetFolder(Folder, out var folder))) return;
        Found(folder!.DeleteTask(taskName, 0));
        if (folder.GetTasks(IncludeHidden).GetCount() == 0 && folder.GetFolders(0).GetCount() == 0)
            RequireFolder(session.Service, Parent).DeleteFolder(instance.Name, 0);
    }

    private IRegisteredTask RequireTask(ITaskService service)
    {
        Marshal.ThrowExceptionForHR(RequireFolder(service, Folder).GetTask(taskName, out var task), -1);
        return task!;
    }

    private static ITaskFolder RequireFolder(ITaskService service, string path)
    {
        Marshal.ThrowExceptionForHR(service.GetFolder(path, out var folder), -1);
        return folder!;
    }

    private static ITaskFolder CreateFolder(ITaskFolder parent, string name, TaskVariant security)
    {
        var hr = parent.CreateFolder(name, security, out var folder);
        if (hr == AlreadyExists) parent.GetFolder(name, out folder); // Two instances can create the common parent.
        else Marshal.ThrowExceptionForHR(hr, -1);
        return folder!;
    }

    // All calls on an RCW stay on the thread/apartment which obtained it.
    private sealed class SchedulerSession : IDisposable
    {
        internal ITaskService Service { get; }
        private readonly bool initialized;

        internal SchedulerSession(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var hr = CoInitializeEx(0, 0); // COINIT_MULTITHREADED
            if (hr != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(hr, -1); // An existing STA also works.
            initialized = hr >= 0;
            try
            {
                Guid clsid = new("0F87369F-A4E5-4CFC-BD3E-73E6154572DD");
                var iid = typeof(ITaskService).GUID;
                Marshal.ThrowExceptionForHR(CoCreateInstance(in clsid, 0, 1, in iid, out var service), -1);
                Service = service;
                Service.Connect(default, default, default, default);
            }
            catch { Dispose(); throw; }
        }

        public void Dispose() { if (initialized) CoUninitialize(); }
    }

    [LibraryImport("ole32")] private static partial int CoInitializeEx(nint reserved, uint model);
    [LibraryImport("ole32")] private static partial void CoUninitialize();
    [LibraryImport("ole32")] private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context,
        in Guid iid, out ITaskService service);
}
