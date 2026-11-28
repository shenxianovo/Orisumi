using System.Runtime.InteropServices;

namespace Orisumi.App;

internal static unsafe partial class Native
{
    internal const uint WM_NULL = 0x0000, WM_DESTROY = 0x0002, WM_SETTINGCHANGE = 0x001A, WM_CONTEXTMENU = 0x007B,
        WM_DISPLAYCHANGE = 0x007E, WM_TIMER = 0x0113, WM_DPICHANGED = 0x02E0, WM_APP = 0x8000;
    internal const uint NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401;
    internal const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4, NOTIFYICON_VERSION_4 = 4;
    internal const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_SHOWTIP = 0x80;
    internal const uint MF_STRING = 0x0000, MF_GRAYED = 0x0001, MF_SEPARATOR = 0x0800;
    internal const uint TPM_RIGHTBUTTON = 0x0002, TPM_RETURNCMD = 0x0100, TPM_BOTTOMALIGN = 0x0020;
    internal const uint MB_YESNO = 0x04, MB_ICONWARNING = 0x30, MB_ICONERROR = 0x10, MB_ICONINFORMATION = 0x40, MB_DEFBUTTON2 = 0x100,
        MB_SETFOREGROUND = 0x10000, MB_TOPMOST = 0x40000;
    internal const int IDYES = 6, SM_CXSMICON = 49;

    [StructLayout(LayoutKind.Sequential)]
    internal struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public delegate* unmanaged<nint, uint, nuint, nint, nint> lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public char* lpszMenuName, lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public int x, y;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
        public uint dwState, dwStateMask;
        public fixed char szInfo[256];
        public uint uVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ICONINFO
    {
        public int fIcon;
        public uint xHotspot, yHotspot;
        public nint hbmMask, hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GdiplusStartupInput
    {
        public uint GdiplusVersion;
        public nint DebugEventCallback;
        public int SuppressBackgroundThread, SuppressExternalCodecs;
    }

    internal static void SetText(char* destination, int capacity, string text)
    {
        var length = Math.Min(text.Length, capacity - 1);
        text.AsSpan(0, length).CopyTo(new Span<char>(destination, capacity));
        destination[length] = '\0';
    }

    // user32
    [LibraryImport("user32", SetLastError = true)]
    internal static partial ushort RegisterClassExW(WNDCLASSEXW* windowClass);
    [LibraryImport("user32", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateWindowExW(uint extendedStyle, string className, string title, uint style, int x, int y,
        int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [LibraryImport("user32")]
    internal static partial nint DefWindowProcW(nint window, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32")]
    internal static partial int GetMessageW(MSG* message, nint window, uint min, uint max);
    [LibraryImport("user32")]
    internal static partial nint DispatchMessageW(MSG* message);
    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32")]
    internal static partial void PostQuitMessage(int code);
    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(nint window);
    [LibraryImport("user32", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint RegisterWindowMessageW(string name);
    [LibraryImport("user32")]
    internal static partial nuint SetTimer(nint window, nuint id, uint milliseconds, nint callback);
    [LibraryImport("user32")]
    internal static partial nint CreatePopupMenu();
    [LibraryImport("user32", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AppendMenuW(nint menu, uint flags, nuint id, string? text);
    [LibraryImport("user32")]
    internal static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint window, nint parameters);
    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyMenu(nint menu);
    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint window);
    [LibraryImport("user32", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int MessageBoxW(nint window, string text, string caption, uint type);
    [LibraryImport("user32", SetLastError = true)]
    internal static partial nint CreateIconIndirect(ICONINFO* info);
    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(nint icon);
    [LibraryImport("user32")]
    internal static partial uint GetDpiForWindow(nint window);
    [LibraryImport("user32")]
    internal static partial int GetSystemMetricsForDpi(int index, uint dpi);
    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessDpiAwarenessContext(nint context);

    // shell32
    [LibraryImport("shell32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Shell_NotifyIconW(uint message, NOTIFYICONDATAW* data);

    // gdi32
    [LibraryImport("gdi32", SetLastError = true)]
    internal static partial nint CreateDIBSection(nint dc, BITMAPINFOHEADER* info, uint usage, void** bits, nint section, uint offset);
    [LibraryImport("gdi32", SetLastError = true)]
    internal static partial nint CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, void* bits);
    [LibraryImport("gdi32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(nint handle);

    // kernel32
    [LibraryImport("kernel32", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint GetModuleHandleW(string? name);
    [LibraryImport("kernel32")]
    internal static partial uint GetOEMCP();

    // GDI+ flat API: decodes and scales the PNG; everything else is plain pixel work.
    [LibraryImport("gdiplus")]
    internal static partial int GdiplusStartup(out nuint token, GdiplusStartupInput* input, nint output);
    [LibraryImport("gdiplus", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GdipCreateBitmapFromFile(string path, out nint bitmap);
    [LibraryImport("gdiplus")]
    internal static partial int GdipCreateBitmapFromScan0(int width, int height, int stride, int format, byte* scan0, out nint bitmap);
    [LibraryImport("gdiplus")]
    internal static partial int GdipGetImageGraphicsContext(nint image, out nint graphics);
    [LibraryImport("gdiplus")]
    internal static partial int GdipSetInterpolationMode(nint graphics, int mode);
    [LibraryImport("gdiplus")]
    internal static partial int GdipSetPixelOffsetMode(nint graphics, int mode);
    [LibraryImport("gdiplus")]
    internal static partial int GdipDrawImageRectI(nint graphics, nint image, int x, int y, int width, int height);
    [LibraryImport("gdiplus")]
    internal static partial int GdipDeleteGraphics(nint graphics);
    [LibraryImport("gdiplus")]
    internal static partial int GdipDisposeImage(nint image);
}
