using System.Runtime.InteropServices;

namespace Orisumi.Collectors.Windows;

internal static unsafe partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowClass
    {
        internal uint Style;
        internal delegate* unmanaged<nint, uint, nuint, nint, nint> Procedure;
        internal int ClassExtra, WindowExtra;
        internal nint Instance, Icon, Cursor, Background, Menu;
        internal char* Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        internal nint Window;
        internal uint Id;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal int X, Y;
        internal uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PowerCallback
    {
        internal delegate* unmanaged<nint, uint, nint, uint> Callback;
        internal nint Context;
    }

    internal struct LastInput { internal uint Size, Tick; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawInputDevice
    {
        internal ushort Page, Usage;
        internal uint Flags;
        internal nint Target;
    }

    internal struct Point { internal int X, Y; }

    [LibraryImport("user32", SetLastError = true)] internal static partial int RegisterRawInputDevices(RawInputDevice* devices, uint count, uint size);
    [LibraryImport("user32", SetLastError = true)] internal static partial uint GetRawInputData(nint input, uint command, void* data, ref uint size, uint headerSize);
    [LibraryImport("user32", SetLastError = true)] internal static partial int GetCursorPos(out Point point);
    [LibraryImport("user32", SetLastError = true)] internal static partial int SetProcessDpiAwarenessContext(nint context);

    [LibraryImport("kernel32", EntryPoint = "GetModuleHandleW")] internal static partial nint GetModuleHandle(nint name);
    [LibraryImport("user32", EntryPoint = "RegisterClassW", SetLastError = true)] internal static partial ushort RegisterClass(WindowClass* windowClass);
    [LibraryImport("user32", EntryPoint = "UnregisterClassW")] internal static partial int UnregisterClass(nint atom, nint instance);
    [LibraryImport("user32", EntryPoint = "CreateWindowExW", SetLastError = true)] internal static partial nint CreateWindowEx(
        uint exStyle, nint atom, nint name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint module, nint param);
    [LibraryImport("user32")] internal static partial int DestroyWindow(nint window);
    [LibraryImport("user32", EntryPoint = "DefWindowProcW")] internal static partial nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32", EntryPoint = "GetMessageW", SetLastError = true)] internal static partial int GetMessage(out Message message, nint window, uint first, uint last);
    [LibraryImport("user32", EntryPoint = "DispatchMessageW")] internal static partial nint DispatchMessage(in Message message);
    [LibraryImport("user32", EntryPoint = "PostMessageW")] internal static partial int PostMessage(nint window, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32")] internal static partial void PostQuitMessage(int exit);
    [LibraryImport("user32", SetLastError = true)] internal static partial nuint SetTimer(nint window, nuint id, uint ms, nint callback);
    [LibraryImport("user32", SetLastError = true)] internal static partial nint SetWinEventHook(uint first, uint last, nint module,
        delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> callback, uint process, uint thread, uint flags);
    [LibraryImport("user32")] internal static partial int UnhookWinEvent(nint hook);

    [LibraryImport("user32")] internal static partial nint GetForegroundWindow();
    [LibraryImport("user32", SetLastError = true)] internal static partial uint GetWindowThreadProcessId(nint window, out uint pid);
    [LibraryImport("user32", SetLastError = true)] internal static partial int InternalGetWindowText(nint window, char* text, int capacity);
    [LibraryImport("user32", SetLastError = true)] internal static partial int EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint data);
    [LibraryImport("user32")] internal static partial int EnumChildWindows(nint parent, delegate* unmanaged<nint, nint, int> callback, nint data);
    [LibraryImport("user32", EntryPoint = "GetClassNameW")] internal static partial int GetClassName(nint window, char* name, int length);
    [LibraryImport("user32")] internal static partial int IsWindow(nint window);
    [LibraryImport("user32")] internal static partial int IsWindowVisible(nint window);
    [LibraryImport("user32")] internal static partial nint GetWindow(nint window, uint relation);
    [LibraryImport("user32", EntryPoint = "GetWindowLongW", SetLastError = true)] internal static partial int GetWindowLong(nint window, int index);
    [LibraryImport("dwmapi")] internal static partial int DwmGetWindowAttribute(nint window, uint attribute, out uint value, uint bytes);

    [LibraryImport("kernel32", SetLastError = true)] internal static partial nint OpenProcess(uint access, int inherit, uint pid);
    [LibraryImport("kernel32")] internal static partial int CloseHandle(nint handle);
    [LibraryImport("kernel32", SetLastError = true)] internal static partial uint WaitForSingleObject(nint handle, uint ms);
    [LibraryImport("kernel32", SetLastError = true)] internal static partial int GetProcessTimes(nint process, out long created, out long exited, out long kernel, out long user);
    [LibraryImport("kernel32", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)] internal static partial int QueryFullProcessImageName(nint process, uint flags, char* text, ref uint capacity);
    [LibraryImport("kernel32")] internal static partial int GetApplicationUserModelId(nint process, ref uint capacity, char* text);
    [LibraryImport("advapi32", SetLastError = true)] internal static partial int OpenProcessToken(nint process, uint access, out nint token);
    [LibraryImport("advapi32", SetLastError = true)] internal static partial int GetTokenInformation(nint token, int kind, out int value, uint bytes, out uint returned);

    [LibraryImport("user32", SetLastError = true)] internal static partial int GetLastInputInfo(ref LastInput info);
    // LASTINPUTINFO and WinEvent times use the boot tick count, including sleep.
    [LibraryImport("kernel32")] internal static partial long GetTickCount64();
    [LibraryImport("api-ms-win-core-realtime-l1-1-1.dll")] internal static partial void QueryInterruptTime(out long time);
    [LibraryImport("wtsapi32", SetLastError = true)] internal static partial int WTSRegisterSessionNotification(nint window, uint flags);
    [LibraryImport("wtsapi32")] internal static partial int WTSUnRegisterSessionNotification(nint window);
    [LibraryImport("wtsapi32", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)] internal static partial int WTSQuerySessionInformation(nint server, uint session, int kind, out nint data, out uint length);
    [LibraryImport("wtsapi32")] internal static partial void WTSFreeMemory(nint data);
    [LibraryImport("user32", SetLastError = true)] internal static partial nint RegisterPowerSettingNotification(nint window, in Guid setting, uint flags);
    [LibraryImport("user32")] internal static partial int UnregisterPowerSettingNotification(nint registration);
    [LibraryImport("powrprof")] internal static partial uint PowerRegisterSuspendResumeNotification(uint flags, in PowerCallback callback, out nint registration);
    [LibraryImport("powrprof")] internal static partial uint PowerUnregisterSuspendResumeNotification(nint registration);
}
