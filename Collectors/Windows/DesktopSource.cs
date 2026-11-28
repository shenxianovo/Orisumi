using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Orisumi.Collectors.Windows;

internal sealed unsafe class DesktopSource : IDesktopSource, IDisposable
{
    internal Action<string, string, int> ReadStatus { get; set; } = (_, _, _) => { };
    private sealed class Tracked(nint handle, uint pid)
    {
        internal nint Handle => handle;
        internal ProcessFacts Facts = new(Guid.NewGuid().ToString("N"), pid, null, null, null, null, null);
    }

    private readonly Dictionary<uint, Tracked> tracked = [];

    public nint Foreground() => Native.GetForegroundWindow();

    public bool WindowExists(nint hwnd)
    {
        var exists = Native.IsWindow(hwnd) != 0;
        ReadStatus("windows", "foreground HWND", exists ? 0 : 1400);
        return exists;
    }

    public string? Title(nint hwnd)
    {
        for (var capacity = 256; ; capacity *= 2)
        {
            var buffer = new char[capacity];
            fixed (char* text = buffer)
            {
                var count = Native.InternalGetWindowText(hwnd, text, capacity);
                var error = count == 0 ? Marshal.GetLastPInvokeError() : 0;
                if (error == 0 && count == 0 && Native.IsWindow(hwnd) == 0) error = 1400;
                ReadStatus("window_title", "InternalGetWindowText", error);
                if (error != 0) return null;
                if (count < capacity - 1) return new string(text, 0, count);
            }
        }
    }

    public ProcessFacts? Owner(nint hwnd)
    {
        var pid = ApplicationPid(hwnd);
        return pid == 0 ? null : Track(pid)?.Facts;
    }

    private uint ApplicationPid(nint hwnd)
    {
        if (Native.GetWindowThreadProcessId(hwnd, out var pid) == 0)
        {
            ReadStatus("applications", "GetWindowThreadProcessId", Marshal.GetLastPInvokeError());
            return 0;
        }
        ReadStatus("applications", "GetWindowThreadProcessId", 0);
        // ApplicationFrameHost supplies the frame. The packaged app owns the CoreWindow child.
        if (ClassName(hwnd) == "ApplicationFrameWindow")
        {
            foreach (var child in Enumerate(hwnd))
                if (ClassName(child) == "Windows.UI.Core.CoreWindow" && Native.GetWindowThreadProcessId(child, out var real) != 0 && real != pid)
                    return real;
        }
        return pid;
    }

    private static string ClassName(nint hwnd)
    {
        char* buffer = stackalloc char[256];
        return new string(buffer, 0, Native.GetClassName(hwnd, buffer, 256));
    }

    private static List<nint> Enumerate(nint parent)
    {
        var result = new List<nint>();
        var data = GCHandle.Alloc(result);
        try
        {
            if (parent != 0) Native.EnumChildWindows(parent, &Append, GCHandle.ToIntPtr(data));
            else if (Native.EnumWindows(&Append, GCHandle.ToIntPtr(data)) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        finally { data.Free(); }
        return result;
    }

    [UnmanagedCallersOnly]
    private static int Append(nint hwnd, nint data)
    {
        ((List<nint>)GCHandle.FromIntPtr(data).Target!).Add(hwnd);
        return 1;
    }

    public IReadOnlyList<AppSample> Applications()
    {
        var visible = new Dictionary<uint, bool?>();
        bool enumerated = false;
        try
        {
            foreach (var hwnd in Enumerate(0))
            {
                if (Native.IsWindowVisible(hwnd) == 0) continue;
                var style = Native.GetWindowLong(hwnd, -20);
                var styleError = style == 0 ? Marshal.GetLastPInvokeError() : 0;
                if ((style & 0x80) != 0 || ((style & 0x40000) == 0 && Native.GetWindow(hwnd, 4) != 0)) continue;
                var hr = Native.DwmGetWindowAttribute(hwnd, 14, out var cloaked, sizeof(uint));
                if (hr == 0 && cloaked != 0) continue;
                var pid = ApplicationPid(hwnd);
                if (pid == 0) continue;
                var error = styleError != 0 ? styleError : hr;
                ReadStatus("applications", $"taskbar window pid={pid}", error);
                if (error == 0)
                {
                    visible[pid] = true;
                    Track(pid);
                }
                else visible.TryAdd(pid, null);
            }
            enumerated = true;
            ReadStatus("applications", "EnumWindows", 0);
        }
        catch (Win32Exception error) { ReadStatus("applications", "EnumWindows", error.NativeErrorCode); }

        var result = new List<AppSample>();
        foreach (var (pid, entry) in tracked.ToArray())
        {
            if (RetireIfExited(pid, entry)) continue;
            FillIdentity(entry);
            result.Add(new(entry.Facts, enumerated ? visible.GetValueOrDefault(pid, false) : null));
        }
        return result;
    }

    private Tracked? Track(uint pid)
    {
        if (tracked.TryGetValue(pid, out var current) && !RetireIfExited(pid, current)) return current;
        // Keep the live process handle even when its metadata is inaccessible.
        var handle = Native.OpenProcess(0x100000 | 0x1000, 0, pid);
        if (handle == 0) handle = Native.OpenProcess(0x100000, 0, pid);
        ReadStatus("applications", $"OpenProcess pid={pid}", handle == 0 ? Marshal.GetLastPInvokeError() : 0);
        if (handle == 0) return null;
        var entry = new Tracked(handle, pid);
        tracked.Add(pid, entry);
        FillIdentity(entry);
        return entry;
    }

    private bool RetireIfExited(uint pid, Tracked entry)
    {
        var wait = Native.WaitForSingleObject(entry.Handle, 0);
        ReadStatus("applications", $"WaitForSingleObject pid={pid}", wait == uint.MaxValue ? Marshal.GetLastPInvokeError() : 0);
        if (wait != 0) return false;
        Native.CloseHandle(entry.Handle);
        tracked.Remove(pid);
        return true;
    }

    private void FillIdentity(Tracked entry)
    {
        var p = entry.Facts;
        if (p.Created.HasValue && p.Path != null && p.Aumid != null && p.Name != null && p.Elevated.HasValue) return;
        if (p.Created == null)
        {
            var ok = Native.GetProcessTimes(entry.Handle, out var created, out _, out _, out _) != 0;
            Status("GetProcessTimes", ok ? 0 : Marshal.GetLastPInvokeError());
            if (ok) p = p with { Created = created };
        }
        if (p.Path == null)
        {
            uint size = 32_768;
            var buffer = new char[size];
            fixed (char* text = buffer)
            {
                var ok = Native.QueryFullProcessImageName(entry.Handle, 0, text, ref size) != 0;
                Status("QueryFullProcessImageName", ok ? 0 : Marshal.GetLastPInvokeError());
                if (ok) p = p with { Path = new string(text, 0, (int)size) };
            }
        }
        if (p.Aumid == null)
        {
            uint size = 0;
            var error = Native.GetApplicationUserModelId(entry.Handle, ref size, null);
            if (error == 122)
            {
                var buffer = new char[size];
                fixed (char* text = buffer)
                {
                    error = Native.GetApplicationUserModelId(entry.Handle, ref size, text);
                    if (error == 0) p = p with { Aumid = new string(text) };
                }
            }
            // APPMODEL_ERROR_NO_APPLICATION means this process explicitly has no application identity.
            if (error == 15703) { p = p with { Aumid = "" }; error = 0; }
            Status("GetApplicationUserModelId", error);
        }
        if (p.Name == null && p.Path != null)
        {
            try
            {
                var version = FileVersionInfo.GetVersionInfo(p.Path);
                p = p with { Name = !string.IsNullOrWhiteSpace(version.FileDescription) ? version.FileDescription : version.ProductName ?? "" };
                Status("FileVersionInfo", 0);
            }
            catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException)
            {
                Status("FileVersionInfo", error is Win32Exception native ? native.NativeErrorCode : error.HResult & 0xffff);
            }
        }
        if (p.Elevated == null)
        {
            if (Native.OpenProcessToken(entry.Handle, 8, out var token) != 0)
            {
                try
                {
                    var ok = Native.GetTokenInformation(token, 20, out var elevated, 4, out _) != 0;
                    Status("GetTokenInformation", ok ? 0 : Marshal.GetLastPInvokeError());
                    if (ok) p = p with { Elevated = elevated != 0 };
                }
                finally { Native.CloseHandle(token); }
                Status("OpenProcessToken", 0);
            }
            else Status("OpenProcessToken", Marshal.GetLastPInvokeError());
        }
        entry.Facts = p;

        void Status(string op, int error) => ReadStatus("applications", $"{op} pid={p.Pid}", error);
    }

    public Session Session()
    {
        const int infoEx = 25;
        if (Native.WTSQuerySessionInformation(0, uint.MaxValue, infoEx, out var data, out var length) == 0)
        {
            ReadStatus("user_session", "WTSQuerySessionInformation", Marshal.GetLastPInvokeError());
            return default;
        }
        try
        {
            // win-x64 WTSINFOEXW aligns its LEVEL1 union to 8 bytes. The first fields are ID, state and flags.
            var valid = length >= 20 && Marshal.ReadInt32(data) == 1;
            ReadStatus("user_session", "WTSQuerySessionInformation", valid ? 0 : 13);
            if (!valid) return default;
            var flags = Marshal.ReadInt32(data, 16);
            ReadStatus("screen_lock", "WTSSessionInfoEx.SessionFlags", flags is 0 or 1 ? 0 : 13);
            return new(Marshal.ReadInt32(data, 12) == 0, flags switch { 0 => true, 1 => false, _ => null });
        }
        finally { Native.WTSFreeMemory(data); }
    }

    public long? IdleMilliseconds()
    {
        var info = new Native.LastInput { Size = (uint)sizeof(Native.LastInput) };
        var ok = Native.GetLastInputInfo(ref info) != 0;
        ReadStatus("afk", "GetLastInputInfo", ok ? 0 : Marshal.GetLastPInvokeError());
        return ok ? unchecked((uint)Native.GetTickCount64() - info.Tick) : null;
    }

    public Zone? TimeZone()
    {
        try
        {
            TimeZoneInfo.ClearCachedData();
            var zone = TimeZoneInfo.Local;
            ReadStatus("time_zone", "TimeZoneInfo.Local", 0);
            if (zone.HasIanaId) return new(zone.Id, "iana");
            return TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? new(iana, "iana") : new(zone.Id, "windows");
        }
        catch (Exception error) when (error is InvalidTimeZoneException or TimeZoneNotFoundException)
        {
            ReadStatus("time_zone", "TimeZoneInfo.Local", error.HResult);
            return null;
        }
    }

    public long MonotonicNanoseconds() { Native.QueryInterruptTime(out var time); return time * 100; }

    public void Dispose()
    {
        foreach (var entry in tracked.Values) Native.CloseHandle(entry.Handle);
        tracked.Clear();
    }
}
