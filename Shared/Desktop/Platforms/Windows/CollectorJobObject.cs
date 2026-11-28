using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Orisumi.Desktop;

// CreateProcess attaches the child to the job and installs its log handles before any child code runs.
internal sealed unsafe class CollectorJobObject : IDisposable
{
    private readonly SafeFileHandle job = CreateJobObjectW(0, 0);

    internal CollectorJobObject()
    {
        try
        {
            if (job.IsInvalid) throw LastError();
            var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job, 9, &limits, sizeof(ExtendedLimits))) throw LastError();
        }
        catch { job.Dispose(); throw; }
    }

    internal Process Start(JobSpec spec)
    {
        using var input = Open("NUL", 0x80000000, 3); // GENERIC_READ, OPEN_EXISTING
        using var output = Log(spec.StandardOutput);
        using var error = Log(spec.StandardError);
        nuint size = 0;
        InitializeProcThreadAttributeList(0, 2, 0, ref size);
        var attributes = Marshal.AllocHGlobal((nint)size);
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 2, 0, ref size)) throw LastError();
            try
            {
                var jobHandle = job.DangerousGetHandle();
                nint* inherited = stackalloc nint[] { input.DangerousGetHandle(), output.DangerousGetHandle(), error.DangerousGetHandle() };
                if (!UpdateProcThreadAttribute(attributes, 0, 0x2000D, &jobHandle, (nuint)sizeof(nint), 0, 0) ||
                    !UpdateProcThreadAttribute(attributes, 0, 0x20002, inherited, (nuint)(3 * sizeof(nint)), 0, 0))
                    throw LastError();
                var start = new StartupInfoEx
                {
                    Size = sizeof(StartupInfoEx), Flags = 0x100, Input = inherited[0], Output = inherited[1],
                    Error = inherited[2], Attributes = attributes,
                };
                var command = string.Join(' ', spec.Command.Select(Quote));
                // CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT | CREATE_SUSPENDED. No shell.
                if (!CreateProcessW(spec.Command[0], new StringBuilder(command), 0, 0, true, 0x08080004, 0,
                    spec.WorkingDirectory, ref start, out var info)) throw LastError();
                using var nativeProcess = new SafeProcessHandle(info.Process, true);
                using var thread = new SafeFileHandle(info.Thread, true);
                try
                {
                    var child = Process.GetProcessById(info.Pid);
                    try
                    {
                        _ = child.Handle; // retain a stable process handle, even for an immediate exit
                        if (ResumeThread(thread) == uint.MaxValue) throw LastError();
                        return child;
                    }
                    catch { child.Dispose(); throw; }
                }
                catch { TerminateProcess(nativeProcess, 1); throw; }
            }
            finally { DeleteProcThreadAttributeList(attributes); }
        }
        finally { Marshal.FreeHGlobal(attributes); }
    }

    private static string Quote(string argument)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static SafeFileHandle Log(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return Open(path, 4, 4); // FILE_APPEND_DATA, OPEN_ALWAYS
    }

    private static SafeFileHandle Open(string path, uint access, uint disposition)
    {
        var security = new SecurityAttributes { Size = sizeof(SecurityAttributes), Inherit = 1 };
        var handle = CreateFileW(path, access, 7, ref security, disposition, 0x80, 0);
        if (handle.IsInvalid) throw LastError();
        return handle;
    }

    public void Dispose() => job.Dispose();
    private static Win32Exception LastError() => new(Marshal.GetLastPInvokeError());

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        internal long ProcessTime, JobTime;
        internal uint Flags;
        internal nuint MinWorkingSet, MaxWorkingSet;
        internal uint ActiveProcessLimit;
        internal nuint Affinity;
        internal uint Priority, Scheduling;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        internal BasicLimits Basic;
        internal fixed ulong IoCounters[6];
        internal nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { internal int Size; internal nint Descriptor; internal int Inherit; }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        internal int Size;
        internal nint Reserved, Desktop, Title;
        internal uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
        internal ushort Show, ReservedSize;
        internal nint ReservedBytes, Input, Output, Error, Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { internal nint Process, Thread; internal int Pid, ThreadId; }

    [DllImport("kernel32", ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(nint security, nint name);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ExtendedLimits* limits, int size);
    [DllImport("kernel32", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, ref SecurityAttributes security,
        uint disposition, uint flags, nint template);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint size);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, void* value, nuint size, nint previous, nint returned);
    [DllImport("kernel32")]
    private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool CreateProcessW(string application, StringBuilder command, nint processAttributes, nint threadAttributes,
        bool inherit, uint flags, nint environment, string directory, ref StartupInfoEx start, out ProcessInformation process);
    [DllImport("kernel32", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32")]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint code);
}
