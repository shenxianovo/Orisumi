using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Orisumi.Desktop;

internal sealed class SessionWindow : IDisposable
{
    private readonly Thread thread;
    private readonly TaskCompletionSource<nint> created = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action ending;
    private readonly WindowProcedure procedure;
    private nint original;

    internal SessionWindow(Action ending)
    {
        this.ending = ending;
        procedure = Handle;
        thread = new Thread(Pump) { IsBackground = true, Name = "Orisumi session messages" };
        thread.Start();
        created.Task.GetAwaiter().GetResult();
    }

    private void Pump()
    {
        var window = CreateWindowExW(0, "STATIC", "Orisumi Capture", 0, 0, 0, 0, 0, 0, 0, 0, 0);
        if (window == 0) { created.SetException(new Win32Exception(Marshal.GetLastPInvokeError())); return; }
        try
        {
            original = SetWindowLongPtrW(window, -4, Marshal.GetFunctionPointerForDelegate(procedure));
            if (original == 0) { created.SetException(new Win32Exception(Marshal.GetLastPInvokeError())); return; }
            created.SetResult(window);
            while (GetMessageW(out var message, 0, 0, 0) > 0) DispatchMessageW(ref message);
        }
        finally { DestroyWindow(window); }
    }

    private nint Handle(nint window, uint message, nuint wParam, nint lParam)
    {
        if (message == 0x11) return 1; // WM_QUERYENDSESSION. A later FALSE WM_ENDSESSION cancels the shutdown.
        if (message == 0x16 && wParam != 0) { ending(); return 0; }
        if (message == 0x8000) { PostQuitMessage(0); return 0; }
        return CallWindowProcW(original, window, message, wParam, lParam);
    }

    public void Dispose()
    {
        PostMessageW(created.Task.Result, 0x8000, 0, 0);
        thread.Join();
    }

    private delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        internal nint Window;
        internal uint Id;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal int X, Y;
        internal uint Private;
    }

    [DllImport("user32", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint CreateWindowExW(uint extended, string cls, string title, uint style, int x, int y, int w, int h,
        nint parent, nint menu, nint module, nint param);
    [DllImport("user32", ExactSpelling = true, SetLastError = true)]
    private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
    [DllImport("user32", ExactSpelling = true)]
    private static extern nint CallWindowProcW(nint original, nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32", ExactSpelling = true)]
    private static extern int GetMessageW(out Message message, nint window, uint min, uint max);
    [DllImport("user32", ExactSpelling = true)]
    private static extern nint DispatchMessageW(ref Message message);
    [DllImport("user32", ExactSpelling = true)]
    private static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32")]
    private static extern void PostQuitMessage(int code);
    [DllImport("user32")]
    private static extern bool DestroyWindow(nint window);
}
