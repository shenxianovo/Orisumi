using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Orisumi.Collectors.Windows;

internal sealed unsafe class DesktopLoop(DesktopSource source, WindowsObservations observations, bool windows, bool input,
    Action<Batch> flush)
{
    private const uint RawInput = 0xff, RawInputData = 0x10000003, RawHeaderSize = 24; // RAWINPUTHEADER on x64
    private const uint InputSink = 0x100; // RIDEV_INPUTSINK: also while another window is in the foreground
    private bool physicalPixels;
    private const uint ForegroundChanged = 3, Destroyed = 0x8001, Hidden = 0x8003, NameChanged = 0x800c;
    private const uint Close = 0x10, Timer = 0x113, Power = 0x218, SessionChange = 0x2b1, CallbackPower = 0x8001;
    private static readonly Guid DisplaySetting = new("6fe69556-704a-47a0-8f24-c28d936fda47");
    [ThreadStatic] private static DesktopLoop? current;
    private bool ready;
    private long baseline;
    private long nextRecheck;
    private Exception? failure;

    internal void Run(CancellationToken stop)
    {
        var module = Native.GetModuleHandle(0);
        const string className = "OrisumiWindowsCapture";
        var hooks = new List<nint>();
        nint hwnd = 0, display = 0, suspend = 0;
        ushort atom = 0;
        var sessionRegistered = false;
        current = this;
        try
        {
            // Per-monitor v2 awareness makes GetCursorPos report physical pixels instead of DPI-virtualized ones.
            physicalPixels = Native.SetProcessDpiAwarenessContext(-4) != 0;
            if (input && !physicalPixels) observations.ReadStatus("input", "SetProcessDpiAwarenessContext", Marshal.GetLastPInvokeError());
            fixed (char* name = className)
            {
                var wc = new Native.WindowClass { Procedure = &WindowProc, Name = name, Instance = module };
                atom = Native.RegisterClass(&wc);
            }
            if (atom == 0) throw LastError("RegisterClass");
            hwnd = Native.CreateWindowEx(0, atom, 0, 0, 0, 0, 0, 0, 0, 0, module, 0);
            if (hwnd == 0) throw LastError("CreateWindowEx");
            Hook(ForegroundChanged, ForegroundChanged);
            Hook(Destroyed, Hidden);
            if (windows) Hook(NameChanged, NameChanged);
            sessionRegistered = Native.WTSRegisterSessionNotification(hwnd, 0) != 0;
            if (!sessionRegistered) throw LastError("WTSRegisterSessionNotification");
            display = Native.RegisterPowerSettingNotification(hwnd, DisplaySetting, 0);
            if (display == 0) throw LastError("RegisterPowerSettingNotification");
            var callback = new Native.PowerCallback { Callback = &OnPower, Context = hwnd };
            var error = Native.PowerRegisterSuspendResumeNotification(2, callback, out suspend);
            if (error != 0) throw new Win32Exception((int)error, $"PowerRegisterSuspendResumeNotification: win32={error}");
            if (input)
            {
                var devices = stackalloc Native.RawInputDevice[2];
                devices[0] = new() { Page = 1, Usage = 6, Flags = InputSink, Target = hwnd }; // keyboard
                devices[1] = new() { Page = 1, Usage = 2, Flags = InputSink, Target = hwnd }; // mouse
                var registered = Native.RegisterRawInputDevices(devices, 2, (uint)sizeof(Native.RawInputDevice)) != 0;
                observations.ReadStatus("input", "RegisterRawInputDevices", registered ? 0 : Marshal.GetLastPInvokeError());
                observations.InputRegistered(registered);
            }

            // Hooks have an out-of-context queue on this thread. Re-read foreground after the initial enumeration.
            baseline = Native.GetTickCount64();
            observations.Start();
            ready = true;
            observations.Foreground(source.Foreground());
            nextRecheck = Environment.TickCount64 + 60_000;
            flush(observations.Flush());
            if (Native.SetTimer(hwnd, 1, 1000, 0) == 0) throw LastError("SetTimer");
            using var registration = stop.Register(() => Native.PostMessage(hwnd, Close, 0, 0));
            while (true)
            {
                var received = Native.GetMessage(out var message, 0, 0, 0);
                if (received < 0) throw LastError("GetMessage");
                if (received == 0) break;
                Native.DispatchMessage(message);
            }
            if (failure != null) throw new IOException("Windows observation failed", failure);
            observations.ReleaseKeys();
            flush(observations.Flush());
        }
        finally
        {
            ready = false;
            if (suspend != 0) Native.PowerUnregisterSuspendResumeNotification(suspend);
            if (display != 0) Native.UnregisterPowerSettingNotification(display);
            if (sessionRegistered) Native.WTSUnRegisterSessionNotification(hwnd);
            foreach (var hook in hooks) Native.UnhookWinEvent(hook);
            if (hwnd != 0) Native.DestroyWindow(hwnd);
            if (atom != 0) Native.UnregisterClass(atom, module);
            current = null;
        }

        void Hook(uint first, uint last)
        {
            var hook = Native.SetWinEventHook(first, last, 0, &WinEvent, 0, 0, 0);
            if (hook == 0) throw LastError("SetWinEventHook");
            hooks.Add(hook);
        }
    }

    private static Win32Exception LastError(string operation)
    {
        var error = Marshal.GetLastPInvokeError();
        return new(error, $"{operation}: win32={error}");
    }

    [UnmanagedCallersOnly]
    private static void WinEvent(nint hook, uint kind, nint hwnd, int obj, int child, uint thread, uint tick)
    {
        if (current is not { ready: true } loop || obj != 0 || child != 0 || !loop.ObservationsActive) return;
        try { loop.ObserveWindow(kind, hwnd, tick); }
        catch (Exception error) { loop.Fail(error); }
    }

    private bool ObservationsActive => observations.Observing;

    private void ObserveWindow(uint kind, nint hwnd, uint tick)
    {
        var now = Native.GetTickCount64();
        if (now - unchecked((uint)now - tick) < baseline) return;
        if (kind == ForegroundChanged) observations.Foreground(hwnd);
        else if (kind == NameChanged) observations.Title(hwnd);
        else
        {
            if (kind == Destroyed) observations.Destroyed(hwnd);
            observations.Applications();
        }
    }

    [UnmanagedCallersOnly]
    private static uint OnPower(nint hwnd, uint kind, nint setting)
    {
        Native.PostMessage(hwnd, CallbackPower, kind, 0);
        return 0;
    }

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (current is { } loop)
        {
            try
            {
                if (message == Close) { Native.PostQuitMessage(0); return 0; }
                if (loop.Dispatch(message, wParam, lParam)) return 1;
            }
            catch (Exception error) { loop.Fail(error); return 0; }
        }
        return Native.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void Fail(Exception error)
    {
        failure ??= error;
        ready = false;
        Native.PostQuitMessage(1);
    }

    private bool Dispatch(uint message, nuint kind, nint data)
    {
        // The display registration can deliver its initial value synchronously.
        if (message == Power && kind == 0x8013 && data != 0 && *(Guid*)data == DisplaySetting && *(uint*)(data + 16) >= 4)
        {
            observations.Display(*(uint*)(data + 20) switch { 0 => true, 1 or 2 => false, _ => null });
            return true;
        }
        if (!ready) return false;
        switch (message)
        {
            case RawInput:
                ReadInput(data);
                return false; // DefWindowProc releases the raw input buffer.
            case Timer:
                var now = Environment.TickCount64;
                var recheck = now >= nextRecheck;
                if (recheck)
                {
                    nextRecheck = now + 60_000;
                    var session = source.Session();
                    observations.Lock(session.Locked);
                    ChangeSession(session.Active);
                    observations.TimeZone();
                }
                observations.Reconcile(recheck);
                flush(observations.Flush());
                return true;
            case SessionChange:
                switch (kind)
                {
                    case 1: case 3: case 5: ChangeSession(true); break;
                    case 2: case 4: case 6: ChangeSession(false); break;
                    case 7: observations.Lock(true); break;
                    case 8: observations.Lock(false); break;
                }
                return true;
            case Power:
            case CallbackPower:
                if (kind == 4) observations.Sleep(true);
                else if (kind is 7 or 18)
                {
                    if (!observations.Observing) baseline = Native.GetTickCount64();
                    observations.Sleep(false);
                    observations.Lock(source.Session().Locked);
                }
                return true;
            case 0x1a: observations.TimeZone(); return true; // WM_SETTINGCHANGE
            case 0x1e: observations.TimeZone(); observations.Clock(); return true; // WM_TIMECHANGE
            default: return false;
        }
    }

    private void ReadInput(nint handle)
    {
        const int capacity = 64; // RAWINPUT with a keyboard (40 bytes) or mouse (48 bytes) payload
        var buffer = stackalloc byte[capacity];
        var size = (uint)capacity;
        var read = Native.GetRawInputData(handle, RawInputData, buffer, ref size, RawHeaderSize);
        observations.ReadStatus("input", "GetRawInputData", read == uint.MaxValue ? Marshal.GetLastPInvokeError() : 0);
        if (read == uint.MaxValue) return;
        var body = buffer + RawHeaderSize;
        switch (*(uint*)buffer)
        {
            case 1: // RIM_TYPEKEYBOARD: MakeCode, Flags (1 break, 2 E0, 4 E1), Reserved, VKey
                var flags = *(ushort*)(body + 2);
                observations.Key(*(ushort*)(body + 6), *(ushort*)body, (flags & 6) != 0, (flags & 1) == 0);
                break;
            case 0: // RIM_TYPEMOUSE: usButtonFlags at offset 4; button n goes down at bit 2n and up at bit 2n+1.
                var buttons = *(ushort*)(body + 4);
                (int X, int Y)? cursor = null;
                if ((buttons & 0x3ff) != 0 && physicalPixels)
                {
                    var ok = Native.GetCursorPos(out var point) != 0;
                    observations.ReadStatus("input", "GetCursorPos", ok ? 0 : Marshal.GetLastPInvokeError());
                    if (ok) cursor = (point.X, point.Y);
                }
                for (var button = 0; button < 5; button++)
                {
                    if ((buttons & (1 << 2 * button)) != 0) observations.MouseButton(button, true, cursor);
                    if ((buttons & (1 << 2 * button + 1)) != 0) observations.MouseButton(button, false, cursor);
                }
                observations.Activity();
                break;
        }
    }

    private void ChangeSession(bool? active)
    {
        if (active == true && !observations.Observing) baseline = Native.GetTickCount64();
        observations.SessionChanged(active);
    }
}
