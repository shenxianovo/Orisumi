#:project ../Host/Platforms/Windows/App/Orisumi.App.Windows.csproj
#:property AssemblyName=WindowsAppChecks
#:property PublishAot=false
#:property AllowUnsafeBlocks=true

using Orisumi.App;

// Where the tray finds the CLI and the instance.
var installed = AppConfiguration.Parse([], @"C:\Users\me\AppData\Local\Orisumi-dev\app\host\orisumi-app.exe");
Check(installed == new AppConfiguration(@"C:\Users\me\AppData\Local\Orisumi-dev\app\host\orisumi.exe",
    @"C:\Users\me\AppData\Local\Orisumi-dev", true), "installed tray reads the CLI and store around it");
Check(installed.Executable == @"C:\Users\me\AppData\Local\Orisumi-dev\app\host\orisumi-app.exe", "relaunch uses the installed path");
var checkout = AppConfiguration.Parse(["--cli", @"C:\h\orisumi.exe", "--store", @"C:\s"], @"E:\Code\Orisumi\bin\orisumi-app.exe");
Check(checkout == new AppConfiguration(@"C:\h\orisumi.exe", @"C:\s", false), "options pick the host and store");
Check(!AppConfiguration.Parse(["--store", @"C:\s"], @"C:\s\app\host\orisumi-app.exe").Installed, "options mean not installed");
foreach (var arguments in new string[][] { [], ["--cli"], ["--cli", "a", "--cli", "b"], ["--other", "x"] })
    Check(Throws(() => AppConfiguration.Parse(arguments, @"E:\Code\Orisumi\bin\orisumi-app.exe")), $"rejects [{string.Join(' ', arguments)}] from a checkout");

// Words, matching the macOS app.
Check(Presentation.Title("prod") == "Orisumi" && Presentation.Title("dev") == "Orisumi Dev" && Presentation.Title("lab") == "Orisumi (lab)", "titles");
Check(Presentation.DisplayName("claude") == "Claude Code" && Presentation.DisplayName("windows") == "Windows" &&
    Presentation.DisplayName("some_source") == "Some Source", "display names");
Check(Presentation.Elapsed(0, 42_000) == "42s" && Presentation.Elapsed(0, 5 * 60_000 + 3_000) == "5m" &&
    Presentation.Elapsed(0, 65 * 60_000) == "1h 5m" && Presentation.Elapsed(0, (51 * 60 + 7) * 60_000) == "2d 3h", "elapsed");

var status = Presentation.Parse("""
    {"instance":"dev","host_version":"dev+abc","generated_at_utc_ms":1000000,"health":"attention","collectors":[
      {"name":"browser","health":"ok","problems":[],"agent":{"state":"running","pid":1,"last_exit_code":null},"run":null},
      {"name":"claude","health":"ok","problems":[],"agent":{"state":"running"},"run":{"run_id":3,"started_at_utc_ms":1,"stopped_at_utc_ms":null}},
      {"name":"codex","health":"attention","problems":["stale"],"agent":{"state":"running","last_exit_code":"terminated"},
       "run":{"run_id":4,"started_at_utc_ms":1,"confirmed_until_utc_ms":700000,"stopped_at_utc_ms":null},"read_failures_last_hour":0},
      {"name":"windows","health":"stopped","problems":["not_loaded"],"agent":{"state":"not_loaded"},"run":null}]}
    """);
Check(status.HostVersion == "dev+abc" && Presentation.HealthOf(status.Health) == Health.Attention, "parses the status");
Check(Presentation.Summary(status.Collectors[0], status.GeneratedAtUtcMs) == "Waiting for Chrome", "browser without a run waits");
Check(Presentation.Summary(status.Collectors[1], status.GeneratedAtUtcMs) == "Capturing", "open run captures");
Check(Presentation.Summary(status.Collectors[2], status.GeneratedAtUtcMs) == "No confirmation for 5m", "stale says for how long");
Check(Presentation.Summary(status.Collectors[3], status.GeneratedAtUtcMs) == "Stopped", "not loaded is stopped");
Check(Presentation.Headline(status) == "2 sources need attention", "headline counts sources that aren't fine");
Check(Presentation.AnyLoaded(status), "a running agent counts as loaded");

CaptureStatus With(params (string Name, string Health, string State)[] collectors) => new()
{
    Health = "ok",
    Collectors = [.. collectors.Select(c => new Collector
        { Name = c.Name, Health = c.Health, Problems = c.Health == "stopped" ? ["not_loaded"] : [], Agent = new() { State = c.State } })],
};
Check(Presentation.Headline(With()) == "No sources installed", "no sources");
Check(Presentation.Headline(With(("claude", "ok", "running"))) == "Claude Code capturing", "one source");
Check(Presentation.Headline(With(("claude", "ok", "running"), ("codex", "ok", "running"))) == "All 2 sources capturing", "all capturing");
var stopped = With(("claude", "stopped", "not_loaded"), ("codex", "stopped", "not_loaded"));
Check(Presentation.Headline(stopped) == "Capture stopped" && !Presentation.AnyLoaded(stopped), "all stopped");
Check(Presentation.Headline(With(("claude", "stopped", "not_loaded"), ("codex", "ok", "running"))) == "Claude Code stopped", "one stopped");
// The supervisor is down: its sources read `not_loaded` but are still wanted, and Stop must stay available.
var orphaned = new CaptureStatus { Collectors = [new() { Name = "claude", Health = "attention", Problems = ["not_running"], Agent = new() { State = "not_loaded" } }] };
Check(Presentation.AnyLoaded(orphaned), "a wanted source counts as loaded while the supervisor is down");

// The icon: colour, faded gray, the attention dot and the instance tag.
var mascot = Path.GetFullPath("Host/Platforms/Windows/App/Icon/AppIcon.png");
using (var plain = new TrayIcons(mascot, tagged: false))
using (var tagged = new TrayIcons(mascot, tagged: true))
{
    foreach (var size in new[] { 16, 20, 24, 32 })
    {
        var ok = plain.Render(Health.Ok, size);
        var faded = plain.Render(Health.Stopped, size);
        var attention = plain.Render(Health.Attention, size);
        var tag = tagged.Render(Health.Ok, size);
        Check(ok.Length == size * size * 4, $"{size}px has every pixel");
        // Pixels, not one spot: the middle of the mascot is her white hair.
        var pixels = Enumerable.Range(0, size * size).Select(i => i * 4).ToList();
        Check(pixels.Any(i => ok[i + 3] == 255 && Math.Max(ok[i], Math.Max(ok[i + 1], ok[i + 2])) - Math.Min(ok[i], Math.Min(ok[i + 1], ok[i + 2])) > 100),
            $"{size}px keeps colour");
        Check(pixels.All(i => faded[i] == faded[i + 1] && faded[i + 1] == faded[i + 2] && faded[i + 3] <= 141), $"{size}px fades to translucent gray");
        var dot = (int)(size - 3.5 * size / 16.0); // the dot's centre, lower right
        var corner = (dot * size + dot) * 4;
        Check(attention[corner + 2] > 200 && attention[corner + 1] is > 100 and < 190 && attention[corner] < 80, $"{size}px dot is orange: {Px(attention, corner)}");
        var unit = size / 16.0;
        int At(double x, double y) => ((int)(y * unit) * size + (int)(x * unit)) * 4;
        var indigo = At(1.6, 4.5);
        Check(tag[indigo + 2] < 120 && tag[indigo] > 170, $"{size}px tag is indigo: {Px(tag, indigo)}");
        var letter = At(3.6, 4.5);
        Check(tag[letter] > 200 && tag[letter + 1] > 200 && tag[letter + 2] > 200, $"{size}px tag has a white D: {Px(tag, letter)}");
        var stoppedTag = tagged.Render(Health.Stopped, size);
        Check(stoppedTag[indigo + 3] > 200, $"{size}px tag stays visible when stopped: {Px(stoppedTag, indigo)}");
        Check(plain.Get(Health.Attention, size) != 0, $"{size}px makes an icon");
    }
}

// Installing a host moves the running tray's directory away; an icon file held open would block the move.
var moving = Directory.CreateTempSubdirectory("orisumi-app-check-");
try
{
    var host = Path.Combine(moving.FullName, "host");
    Directory.CreateDirectory(host);
    File.Copy(mascot, Path.Combine(host, "AppIcon.png"));
    using var running = new TrayIcons(Path.Combine(host, "AppIcon.png"), tagged: false);
    running.Get(Health.Ok, 16);
    Directory.Move(host, Path.Combine(moving.FullName, "retired"));
    running.Get(Health.Stopped, 24);
}
finally { moving.Delete(recursive: true); }

Console.WriteLine("Windows app checks passed.");

static string Px(byte[] pixels, int index) => $"BGRA {pixels[index]},{pixels[index + 1]},{pixels[index + 2]},{pixels[index + 3]}";

static bool Throws(Action action)
{
    try { action(); return false; }
    catch (ArgumentException) { return true; }
}

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
