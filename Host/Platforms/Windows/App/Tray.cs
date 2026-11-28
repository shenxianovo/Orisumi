using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Orisumi.App;

/// The notification-area icon and its menu. Everything runs on the thread that pumps messages; CLI calls run on the
/// thread pool and post their results back. Like the macOS app it holds no capture logic: status comes from
/// `capture status --json`, start and stop call `capture start` / `capture stop`, and quitting leaves capture running.
internal sealed class Tray : IDisposable
{
    private const uint TrayMessage = Native.WM_APP + 1, InvokeMessage = Native.WM_APP + 2;
    private const uint WM_POWERBROADCAST = 0x0218, PBT_APMRESUMEAUTOMATIC = 0x0012;
    private const uint CommandStart = 1, CommandStop = 2, CommandLogs = 3, CommandQuit = 4;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static Tray? current;

    private readonly AppConfiguration configuration;
    private readonly Cli cli;
    private readonly string mascot = Path.Combine(AppContext.BaseDirectory, "AppIcon.png");
    private readonly string version =
        typeof(Tray).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    private readonly ConcurrentQueue<Action> posted = new();
    private readonly uint taskbarCreated = Native.RegisterWindowMessageW("TaskbarCreated");
    private nint window;
    private string? instance;
    private TrayIcons icons;
    private bool added, refreshing, replacing;
    // Bumped when a capture command starts and ends, so a status read from before it can't overwrite what came after.
    private int actions;
    private string? running; // the command in progress: "start", "restart" or "stop"
    private int modal; // a menu or message box is open and runs its own message loop
    private CaptureStatus? status;
    private string? statusError;

    /// Set when an update replaced this program: the caller starts the new one after this one is gone.
    internal bool Replaced { get; private set; }

    internal Tray(AppConfiguration configuration)
    {
        this.configuration = configuration;
        cli = new Cli(configuration);
        instance = configuration.InstanceName();
        icons = new TrayIcons(mascot, Tagged(instance));
    }

    private string Title => Presentation.Title(instance);
    private static bool Tagged(string? instance) => instance is not (null or "" or "prod");

    internal unsafe int Run()
    {
        current = this;
        window = CreateWindow();
        AddIcon();
        Native.SetTimer(window, 1, (uint)Interval.TotalMilliseconds, 0);
        Refresh();
        Native.MSG message;
        while (Native.GetMessageW(&message, 0, 0, 0) > 0) Native.DispatchMessageW(&message);
        return 0;
    }

    private static unsafe nint CreateWindow()
    {
        const string className = "Orisumi.Tray";
        var module = Native.GetModuleHandleW(null);
        fixed (char* name = className)
        {
            var windowClass = new Native.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(Native.WNDCLASSEXW), lpfnWndProc = &Procedure, hInstance = module, lpszClassName = name,
            };
            if (Native.RegisterClassExW(&windowClass) == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }
        // A hidden top-level window, not a message-only one: TaskbarCreated is broadcast to top-level windows only.
        var window = Native.CreateWindowExW(0x80 /* WS_EX_TOOLWINDOW */, className, "Orisumi", 0, 0, 0, 0, 0, 0, 0, module, 0);
        return window != 0 ? window : throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
    }

    [UnmanagedCallersOnly]
    private static nint Procedure(nint window, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (current is { } tray && tray.window == window && tray.Handle(message, wParam, lParam)) return 0;
        }
        catch (Exception error)
        {
            // An exception must not cross into Windows; say what happened and keep the icon alive.
            Native.MessageBoxW(window, error.Message, current?.Title ?? "Orisumi", Native.MB_ICONERROR | Native.MB_SETFOREGROUND | Native.MB_TOPMOST);
            return 0;
        }
        return Native.DefWindowProcW(window, message, wParam, lParam);
    }

    private bool Handle(uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case TrayMessage:
                var trayEvent = (uint)(lParam & 0xFFFF);
                // While a menu or message box is open, a second one would stack on top of it.
                if (modal == 0 && trayEvent is Native.WM_CONTEXTMENU or Native.NIN_SELECT or Native.NIN_KEYSELECT)
                    ShowMenu((short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF));
                return true;
            case InvokeMessage:
                while (posted.TryDequeue(out var action)) action();
                return true;
            case Native.WM_TIMER:
                Refresh();
                return true;
            case WM_POWERBROADCAST:
                if (wParam == PBT_APMRESUMEAUTOMATIC) Refresh();
                return false;
            case Native.WM_DPICHANGED or Native.WM_DISPLAYCHANGE or Native.WM_SETTINGCHANGE:
                UpdateIcon();
                return false;
            case Native.WM_DESTROY:
                RemoveIcon();
                Native.PostQuitMessage(0);
                return true;
            default:
                if (message != taskbarCreated) return false;
                // Explorer restarted and forgot every icon.
                added = false;
                AddIcon();
                return true;
        }
    }

    private void Post(Action action)
    {
        posted.Enqueue(action);
        Native.PostMessageW(window, InvokeMessage, 0, 0);
    }

    private Health Health => status is null ? Health.Unavailable : Presentation.HealthOf(status.Health);

    private string Headline => running switch
    {
        "start" => "Starting capture…",
        "restart" => "Restarting capture…",
        "stop" => "Stopping capture…",
        _ => status is not null ? Presentation.Headline(status)
            : statusError is null ? "Checking status…" : "Can't read Orisumi's status",
    };

    private void Refresh()
    {
        if (refreshing || running is not null) return;
        refreshing = true;
        var started = actions;
        _ = Task.Run(async () =>
        {
            CaptureStatus? result = null;
            string? error = null;
            try { result = await cli.Status(); }
            catch (Exception failure) when (failure is CliException or System.Text.Json.JsonException) { error = failure.Message; }
            Post(() =>
            {
                refreshing = false;
                // A capture command ran meanwhile: this reading may predate it.
                if (started != actions) { Refresh(); return; }
                (status, statusError) = (result, error);
                if (result is not null && result.Instance != instance) ChangeInstance(result.Instance);
                replacing = Updated();
                if (!ReplaceWhenIdle()) UpdateIcon();
            });
        });
    }

    /// Started with only `--cli`, the instance shows up with the first status.
    private void ChangeInstance(string name)
    {
        instance = name;
        if (Tagged(name) == icons.Tagged) return;
        icons.Dispose();
        icons = new TrayIcons(mascot, Tagged(name));
    }

    /// An update installs a new host while this program keeps running from the old, moved-away directory.
    private bool Updated() => configuration.Installed && status?.HostVersion is { } installed && installed != version &&
        File.Exists(configuration.Executable);

    private bool ReplaceWhenIdle()
    {
        if (!replacing || modal > 0 || running is not null) return false;
        Replaced = true;
        Native.DestroyWindow(window);
        return true;
    }

    private void Perform(string command)
    {
        running = command;
        actions++;
        UpdateIcon();
        var action = command == "stop" ? "stop" : "start";
        _ = Task.Run(async () =>
        {
            string? error = null;
            try { await cli.Capture(action); }
            catch (CliException failure) { error = failure.Message; }
            Post(() =>
            {
                running = null;
                actions++;
                if (error is not null)
                    Show($"Couldn't {command} capture.\n\n{error}", Native.MB_ICONWARNING);
                Refresh();
            });
        });
    }

    private void ShowMenu(int x, int y)
    {
        // Read now so the icon and tooltip are current by the time the menu closes; the menu shows the last reading.
        Refresh();
        var menu = Native.CreatePopupMenu();
        modal++;
        uint command;
        try
        {
            const uint info = Native.MF_STRING | Native.MF_GRAYED;
            Native.AppendMenuW(menu, info, 0, Title);
            Native.AppendMenuW(menu, info, 0, Headline);
            if (status is null && statusError is not null) Native.AppendMenuW(menu, info, 0, Shorten(statusError));
            if (status is { Collectors.Count: > 0 })
            {
                Native.AppendMenuW(menu, Native.MF_SEPARATOR, 0, null);
                foreach (var line in Lines(status, all: true)) Native.AppendMenuW(menu, info, 0, line);
            }
            Native.AppendMenuW(menu, Native.MF_SEPARATOR, 0, null);
            var supervised = status is not null && Presentation.AnyLoaded(status);
            var idle = running is null;
            Native.AppendMenuW(menu, status is { Collectors.Count: > 0 } && idle ? Native.MF_STRING : Native.MF_GRAYED,
                CommandStart, supervised ? "Restart Capture" : "Start Capture");
            Native.AppendMenuW(menu, supervised && idle ? Native.MF_STRING : Native.MF_GRAYED, CommandStop, "Stop Capture");
            Native.AppendMenuW(menu, configuration.Store is null ? Native.MF_GRAYED : Native.MF_STRING, CommandLogs, "Open Logs Folder");
            Native.AppendMenuW(menu, Native.MF_SEPARATOR, 0, null);
            Native.AppendMenuW(menu, Native.MF_STRING, CommandQuit, $"Quit {Title}");

            // Without the foreground the menu would not close when clicking elsewhere.
            Native.SetForegroundWindow(window);
            command = (uint)Native.TrackPopupMenuEx(menu,
                Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | Native.TPM_BOTTOMALIGN, x, y, window, 0);
            Native.PostMessageW(window, Native.WM_NULL, 0, 0);
        }
        finally
        {
            Native.DestroyMenu(menu);
            modal--;
        }
        Run(command, supervised: status is not null && Presentation.AnyLoaded(status));
        if (!ReplaceWhenIdle()) UpdateIcon();
    }

    private void Run(uint command, bool supervised)
    {
        switch (command)
        {
            case CommandStart:
                Perform(supervised ? "restart" : "start");
                break;
            case CommandStop:
                if (Show($"Stop capture for {Title}?\n\nCollectors stay stopped until you start capture again.",
                        Native.MB_YESNO | Native.MB_ICONWARNING | Native.MB_DEFBUTTON2) == Native.IDYES)
                    Perform("stop");
                break;
            case CommandLogs:
                var logs = Path.Combine(configuration.Store!, "logs");
                if (Directory.Exists(logs)) Process.Start(new ProcessStartInfo(logs) { UseShellExecute = true })?.Dispose();
                else Show($"Logs folder doesn't exist yet.\n\n{logs}", Native.MB_ICONINFORMATION);
                break;
            case CommandQuit:
                Native.DestroyWindow(window);
                break;
        }
    }

    /// The window is hidden, so without TOPMOST a box raised later from a finished command sinks behind other windows.
    private int Show(string text, uint flags)
    {
        modal++;
        try { return Native.MessageBoxW(window, text, Title, flags | Native.MB_SETFOREGROUND | Native.MB_TOPMOST); }
        finally { modal--; }
    }

    /// "Codex — No confirmation for 5m" for every source, or only those that aren't fine.
    private static IEnumerable<string> Lines(CaptureStatus status, bool all) =>
        status.Collectors.Where(collector => all || Presentation.HealthOf(collector.Health) != Health.Ok)
            .Select(collector => $"{Presentation.DisplayName(collector.Name)} — {Presentation.Summary(collector, status.GeneratedAtUtcMs)}");

    private static string Shorten(string text) => text.Length <= 80 ? text : text[..79] + "…";

    /// Title and headline, then the sources that need a look while they fit the 127 characters a tooltip holds.
    private string Tooltip()
    {
        var tip = $"{Title}\n{Headline}";
        if (status is null || running is not null) return tip;
        foreach (var line in Lines(status, all: false))
        {
            if (tip.Length + 1 + line.Length > 127) break;
            tip += "\n" + line;
        }
        return tip;
    }

    private int IconSize => Native.GetSystemMetricsForDpi(Native.SM_CXSMICON, Native.GetDpiForWindow(window));

    private unsafe void AddIcon()
    {
        var data = Data();
        data.uFlags |= Native.NIF_MESSAGE;
        data.uCallbackMessage = TrayMessage;
        // Fails while Explorer is not up yet; TaskbarCreated then adds the icon.
        if (!Native.Shell_NotifyIconW(Native.NIM_ADD, &data)) return;
        data.uVersion = Native.NOTIFYICON_VERSION_4;
        Native.Shell_NotifyIconW(Native.NIM_SETVERSION, &data);
        added = true;
    }

    private unsafe void UpdateIcon()
    {
        if (!added) { AddIcon(); return; }
        var data = Data();
        Native.Shell_NotifyIconW(Native.NIM_MODIFY, &data);
    }

    private unsafe void RemoveIcon()
    {
        if (!added) return;
        var data = new Native.NOTIFYICONDATAW { cbSize = (uint)sizeof(Native.NOTIFYICONDATAW), hWnd = window, uID = 1 };
        Native.Shell_NotifyIconW(Native.NIM_DELETE, &data);
        added = false;
    }

    private unsafe Native.NOTIFYICONDATAW Data()
    {
        var data = new Native.NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(Native.NOTIFYICONDATAW), hWnd = window, uID = 1,
            uFlags = Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP, hIcon = icons.Get(Health, IconSize),
        };
        Native.SetText(data.szTip, 128, Tooltip());
        return data;
    }

    public void Dispose()
    {
        icons.Dispose();
        current = null;
    }
}
