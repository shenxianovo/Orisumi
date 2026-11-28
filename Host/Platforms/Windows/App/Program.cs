using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Orisumi.App;

Native.SetProcessDpiAwarenessContext(-4); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
AppConfiguration configuration;
try { configuration = AppConfiguration.Parse(args, Environment.ProcessPath!); }
catch (ArgumentException error)
{
    Native.MessageBoxW(0, error.Message, "Orisumi", Native.MB_ICONERROR);
    return 2;
}

// One icon per instance and user session; a second start (logon task plus a manual one) just leaves.
var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((configuration.Store ?? configuration.Cli).ToUpperInvariant())))[..16];
using var single = new Mutex(true, $@"Local\Orisumi.App.{key}", out var first);
if (!first) return 0;

bool replaced;
using (var tray = new Tray(configuration))
{
    tray.Run();
    replaced = tray.Replaced;
}
single.ReleaseMutex();
if (replaced) Process.Start(new ProcessStartInfo(configuration.Executable) { UseShellExecute = false })?.Dispose();
return 0;
