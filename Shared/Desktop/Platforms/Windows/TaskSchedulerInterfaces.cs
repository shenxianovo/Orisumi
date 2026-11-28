using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Orisumi.Desktop;

// taskschd.h declares dual interfaces. The four IDispatch slots precede the typed
// methods. Keep SDK order, including the members between calls used by CaptureTask.
[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("00020400-0000-0000-C000-000000000046")]
internal partial interface ISchedulerDispatch
{
    void GetTypeInfoCount(out uint count);
    void GetTypeInfo(uint index, uint locale, out nint info);
    void GetIDsOfNames(in Guid iid, nint names, uint count, uint locale, nint ids);
    void Invoke(int id, in Guid iid, uint locale, ushort flags, nint args, nint result, nint error, nint badArgument);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("2FABA4C7-4DA9-4013-9697-20CC3FD40F85")]
internal partial interface ITaskService : ISchedulerDispatch
{
    [PreserveSig] int GetFolder([MarshalAs(UnmanagedType.BStr)] string path, out ITaskFolder? folder);
    void GetRunningTasks(int flags, out nint collection);
    void NewTask(uint flags, out nint definition);
    void Connect(TaskVariant server, TaskVariant user, TaskVariant domain, TaskVariant password);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("8CFAC062-A080-4C15-9A88-AA7C2AF80DFC")]
internal partial interface ITaskFolder : ISchedulerDispatch
{
    void GetName(out nint bstr);
    void GetPath(out nint bstr);
    void GetFolder([MarshalAs(UnmanagedType.BStr)] string path, out ITaskFolder folder);
    ITaskFolderCollection GetFolders(int flags);
    [PreserveSig] int CreateFolder([MarshalAs(UnmanagedType.BStr)] string name, TaskVariant security, out ITaskFolder? folder);
    void DeleteFolder([MarshalAs(UnmanagedType.BStr)] string name, int flags);
    [PreserveSig] int GetTask([MarshalAs(UnmanagedType.BStr)] string name, out IRegisteredTask? task);
    IRegisteredTaskCollection GetTasks(int flags);
    [PreserveSig] int DeleteTask([MarshalAs(UnmanagedType.BStr)] string name, int flags);
    IRegisteredTask RegisterTask([MarshalAs(UnmanagedType.BStr)] string name, [MarshalAs(UnmanagedType.BStr)] string xml,
        int flags, TaskVariant user, TaskVariant password, int logon, TaskVariant security);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("9C86F320-DEE3-4DD1-B972-A303F26B061E")]
internal partial interface IRegisteredTask : ISchedulerDispatch
{
    void GetName(out nint bstr);
    void GetPath(out nint bstr);
    void GetState(out int state);
    void GetEnabled(out short enabled);
    void PutEnabled(short enabled);
    void Run(TaskVariant parameters, out nint runningTask);
    void RunEx(TaskVariant parameters, int flags, int session, nint userBstr, out nint runningTask);
    void GetInstances(int flags, out nint collection);
    void GetLastRunTime(out double date);
    void GetLastTaskResult(out int result);
    void GetNumberOfMissedRuns(out int count);
    void GetNextRunTime(out double date);
    void GetDefinition(out nint definition);
    [return: MarshalAs(UnmanagedType.BStr)] string GetXml();
    void GetSecurityDescriptor(int info, out nint bstr);
    void SetSecurityDescriptor(nint bstr, int flags);
    void Stop(int flags);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("79184A66-8664-423F-97F1-637356A5D812")]
internal partial interface ITaskFolderCollection : ISchedulerDispatch
{
    int GetCount();
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("86627EB4-42A7-41E4-A4D9-AC33A72F2D52")]
internal partial interface IRegisteredTaskCollection : ISchedulerDispatch
{
    int GetCount();
}

// VARIANT's eight-byte header is followed by a union as large as the two BRECORD
// pointers. Task Scheduler needs only VT_EMPTY and a borrowed VT_BSTR here.
[StructLayout(LayoutKind.Sequential)]
internal readonly struct TaskVariant(nint bstr)
{
    private readonly long header = 8; // VT_BSTR
    private readonly nint value = bstr;
    private readonly nint recordInfo = 0;
}
