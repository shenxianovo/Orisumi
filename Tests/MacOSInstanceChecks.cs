#:project ../Host/Cli/Orisumi.Cli.csproj
#:project ../Tools/Dev/Orisumi.Dev.csproj
#:property AssemblyName=InstanceChecks
#:property PublishAot=false

using System.Runtime.Versioning;
using System.Text.Json;
using System.Xml.Linq;
using Orisumi.Cli;
using Orisumi.Cli.Capture;
using Orisumi.Dev;
using Orisumi.Desktop;
using Orisumi.Platform;

[assembly: SupportedOSPlatform("macos")]

var checkout = Environment.CurrentDirectory;
var temp = Directory.CreateTempSubdirectory("orisumi-mac-instance-");
try
{
    Resolution();
    await Install();
    await BuildBundle();
}
finally { temp.Delete(recursive: true); }

void Resolution()
{
    var prod = Instance.Create(Under("prod"), "prod");
    var dev = Instance.Create(Under("dev"), "dev");
    Check(prod.CaptureAgentLabel("codex") == "dev.orisumi.prod.capture.codex" &&
        prod.BundleIdentifier == "dev.orisumi" && prod.CommandName == "orisumi", "prod names");
    Check(dev.CaptureAgentLabel("browser") == "dev.orisumi.dev.capture.browser" &&
        dev.AppAgentLabel == "dev.orisumi.dev.app" &&
        dev.BundleIdentifier == "dev.orisumi.canary" && dev.CommandName is null, "dev names");
    var other = Instance.Create(Under("other"), "test_codex");
    Check(other.CommandName is null && other.BundleIdentifier == dev.BundleIdentifier, "test instance names");
    Check(Instance.ProductionRoot().EndsWith("/Library/Application Support/Orisumi") &&
        Instance.DevelopmentRoot().EndsWith("/Library/Application Support/Orisumi-dev"), "default roots");
    Check(ChromeExtension.Id("/Users/quq/Orisumi/src/Collectors/Orisumi.Collectors.Browser/ChromeExtension") ==
        "jbdbjgpholjhaneogagghkkgbailcehi", "known Chrome extension ID vector");
    Console.WriteLine("PASS: macOS roots, bundle IDs, launchd labels and command links");
}

async Task Install()
{
    var fixture = Prepare("install system");
    var locations = Locations(fixture);
    var instance = Instance.Create(Under("install store '中文"), "dev");
    var store = instance.Store;
    using var output = new StringWriter();
    var first = Package("first", instance, null, ("host", 'a', "unused"), ("browser", 'b', "capture"), ("codex", 'c', "capture"));
    await Apply(first);
    Check(output.ToString().Contains("host: new dev+") && !Directory.Exists(locations.Commands) &&
        LaunchEvents().SequenceEqual(["bootstrap dev.orisumi.dev.app"]), "new host loads only the GUI when capture is stopped");
    var gui = Path.Combine(locations.Agents, instance.AppAgentLabel + ".plist");
    var fields = PlistFields(gui);
    Check(fields["RunAtLoad"].Name == "true" && !fields.ContainsKey("KeepAlive") &&
        fields["AssociatedBundleIdentifiers"].Elements().Single().Value == instance.BundleIdentifier &&
        fields["ProgramArguments"].Elements().Select(value => value.Value).SequenceEqual([HostBundle.AppExecutable(store.Host), "--menu-bar"]),
        "dev GUI agent uses its signed bundle and keeps the login behavior");
    await CaptureStartup.Start(instance, new MacCollectorJobs(instance, locations), output, default);
    Check(output.ToString().Contains($"Started {instance.CaptureAgentLabel("browser")}") &&
        output.ToString().Contains($"Started {instance.CaptureAgentLabel("codex")}"), "start prints the native registration names");
    await Launchd.StopAgent(Agent("codex"), locations, default);
    ClearEvents();
    var sentinel = Path.Combine(store.Collector("browser"), "keep");
    File.WriteAllText(sentinel, "unchanged");
    var same = Package("same", instance, "v5.0.0", ("host", 'a', "unused"), ("browser", 'b', "capture"));
    output.GetStringBuilder().Clear();
    await Apply(same);
    Check(LaunchEvents().Length == 0 && File.Exists(sentinel) && output.ToString().Contains("browser: unchanged dev+") &&
        ReleaseManifest.Read(store.Manifest).Components.ContainsKey("codex"), "identical and omitted components keep their installed versions and processes");

    var changed = Package("collectors changed", instance, null, ("browser", 'd', "new-args"), ("codex", 'e', "new-stopped"));
    await Apply(changed);
    Check(!File.Exists(sentinel) && LaunchEvents().SequenceEqual(["bootout dev.orisumi.dev.capture.browser", "bootstrap dev.orisumi.dev.capture.browser"]) &&
        !File.Exists(Agent("codex")) && PlistFields(Agent("browser"))["ProgramArguments"].Elements().Last().Value == "new-args",
        "loaded replacements restart with the new describe arguments and stopped replacements stay stopped");

    ClearEvents();
    var newCollector = Package("another collector", instance, null, ("claude", 'f', "capture"));
    await Apply(newCollector);
    Check(LaunchEvents().SequenceEqual(["bootstrap dev.orisumi.dev.capture.claude"]), "a new collector starts while any collector is loaded");
    ClearEvents();
    var newHost = Package("new host", instance, null, ("host", '1', "unused"));
    await Apply(newHost);
    Check(LaunchEvents().SequenceEqual(["kickstart dev.orisumi.dev.app"]) &&
        File.Exists(Agent("browser") + ".loaded") && File.Exists(Agent("claude") + ".loaded"), "host replacement restarts only the GUI");

    ClearEvents();
    var previous = File.ReadAllText(store.Manifest);
    var badHost = Package("bad combined release", instance, null, ("host", '2', "unused"), ("browser", '3', "capture"));
    var hostDirectory = ReleaseManifest.ComponentDirectory(badHost, "host");
    var info = Path.Combine(hostDirectory, HostBundle.Name, "Contents", "Info.plist");
    var original = File.ReadAllText(info);
    var releasePath = Path.Combine(badHost, ReleaseManifest.FileName);
    var manifest = ReleaseManifest.Read(releasePath);
    File.WriteAllText(info, original.Replace(instance.BundleIdentifier, "dev.orisumi"));
    RehashHost();
    await ExpectAsync<InvalidDataException>(() => Apply(badHost), "expected identifier");
    File.WriteAllText(info, original);
    File.WriteAllText(Path.Combine(hostDirectory, HostBundle.Name, "invalid-signature"), "");
    RehashHost();
    await ExpectAsync<InvalidDataException>(() => Apply(badHost), "strict signature verification failed");
    Check(File.ReadAllText(store.Manifest) == previous && LaunchEvents().Length == 0 &&
        (await ProgramDescription.Read(store.HostExecutable, [], default, FixtureTools(fixture).Run)).Version == "dev+" + new string('1', 40),
        "invalid releases leave the installed components intact");

    var failedStart = Package("failed start", instance, null, ("browser", '6', "capture"));
    File.WriteAllText(Path.Combine(fixture, "fail-once"), instance.CaptureAgentLabel("browser"));
    await ExpectAsync<IOException>(() => Apply(failedStart), "bootstrap");
    Check(File.ReadAllText(store.Manifest) == previous && File.Exists(Agent("browser") + ".loaded") &&
        PlistFields(Agent("browser"))["ProgramArguments"].Elements().Last().Value == "new-args", "failed restart rolls back files, manifest and the previous agent");
    await Apply(failedStart);
    Check(ReleaseManifest.Read(store.Manifest).Components["browser"].Identity == new string('6', 40), "rolled back release can be retried");

    await CaptureStartup.Stop(instance, new MacCollectorJobs(instance, locations), output, default);
    ClearEvents();
    var stopped = Package("added while stopped", instance, null, ("macos", '4', "capture"));
    await Apply(stopped);
    Check(LaunchEvents().Length == 0 && !File.Exists(Agent("macos")) && File.Exists(gui + ".loaded"), "adding to a stopped instance leaves capture stopped");

    var prod = Instance.Create(Under("temporary prod"), "prod");
    var prodRelease = Package("prod release", prod, "v5.0.0", ("host", '5', "unused"));
    await InstallCommand.Run(prod, prodRelease, new MacCollectorJobs(prod, locations), new MacInstallation(prod, locations, FixtureTools(fixture)), output, default);
    Check(new FileInfo(Path.Combine(locations.Commands, "orisumi")).LinkTarget == prod.Store.HostExecutable, "only prod installs the command link");
    var certificateFile = Path.Combine(fixture, "certificate.der");
    File.WriteAllText(certificateFile, "fixture certificate");
    await ExpectAsync<InvalidDataException>(() => new MacInstallation(prod, locations, FixtureTools(fixture))
        .VerifyHost(prod.Store.Host, true, default), "证书 SHA-256");
    await new MacInstallation(prod, locations, FixtureTools(fixture), ReleaseManifest.HashFile(certificateFile))
        .VerifyHost(prod.Store.Host, true, default);
    Console.WriteLine("PASS: launchd restarts and rollback, bundle signature, certificate and prod command link");

    Task Apply(string release) => InstallCommand.Run(instance, release, new MacCollectorJobs(instance, locations), new MacInstallation(instance, locations, FixtureTools(fixture)), output, default);
    string Agent(string name) => Launchd.CaptureAgentPlist(instance, locations, name);
    string[] LaunchEvents() => File.Exists(Path.Combine(fixture, "launch-events")) ? File.ReadAllLines(Path.Combine(fixture, "launch-events")) : [];
    void ClearEvents() => File.Delete(Path.Combine(fixture, "launch-events"));
    void RehashHost()
    {
        manifest.Components["host"] = manifest.Components["host"] with { Files = ReleaseManifest.HashFiles(hostDirectory) };
        manifest.Write(releasePath);
    }
}

async Task BuildBundle()
{
    var fixture = Prepare("bundle");
    File.WriteAllText(Path.Combine(fixture, "badge.c"), """
        #include <copyfile.h>
        #include <stddef.h>
        #include <string.h>
        int main(int argc, char **argv) {
            if (argc != 4 || strcmp(argv[1], "--badge-icon") != 0) return 2;
            return copyfile(argv[2], argv[3], NULL, COPYFILE_DATA) != 0;
        }
        """);
    await Repository.Query("/usr/bin/clang", fixture, default, ["badge.c", "-o", "badge"]);
    WriteExecutable(Path.Combine(fixture, "swift"), """
        #!/bin/sh
        set -eu
        [ "$1 $2 $3 $4 $5 $6 $7 $8" = 'build -c release --package-path Host/App.MacOS --product OrisumiApp --scratch-path' ]
        if [ "${10}" = --show-bin-path ]; then printf '%s\n' "$9/release"; exit; fi
        [ "${10} ${11}" = '-Xswiftc -warnings-as-errors' ]
        /bin/mkdir -p "$9/release"
        /bin/cp "$(/usr/bin/dirname "$0")/badge" "$9/release/OrisumiApp"
        """);
    var instance = Instance.Create(Under("bundle instance"), "dev");
    var store = instance.Store;
    Directory.CreateDirectory(Path.GetDirectoryName(store.HostExecutable)!);
    File.Copy("/usr/bin/true", store.HostExecutable);
    var path = Environment.GetEnvironmentVariable("PATH");
    try
    {
        Environment.SetEnvironmentVariable("PATH", fixture + Path.PathSeparator + path);
        await new MacHostBuilder(FixtureTools(fixture)).Complete(checkout, store.Host, instance.Name, "1.0", "dev+fixture", default);
        var hostFields = PlistFields(Path.Combine(store.Host, HostBundle.Name, "Contents", "Info.plist"));
        Check(hostFields["CFBundleIdentifier"].Value == instance.BundleIdentifier &&
            hostFields["CFBundleExecutable"].Value == HostBundle.AppExecutableName &&
            hostFields["CFBundleIconFile"].Value == "AppIcon.icns" && hostFields["LSUIElement"].Name == "true" &&
            hostFields["CFBundleVersion"].Value == "dev+fixture" &&
            hostFields["CFBundleName"].Value == "Orisumi Dev" && hostFields["CFBundleDisplayName"].Value == "Orisumi Dev" &&
            File.Exists(Path.Combine(store.Host, HostBundle.Name, "Contents", "Resources", "AppIcon.icns")) &&
            File.Exists(Path.Combine(store.Host, HostBundle.Name, "Contents", "Resources", "Mascot.png")), "deploy builds the dev app name, badged icon, menu bar mascot");
    }
    finally { Environment.SetEnvironmentVariable("PATH", path); }
}

string Package(string directory, Instance instance, string? release, params (string Name, char Id, string Argument)[] programs)
{
    var path = Directory.CreateDirectory(Under(directory)).FullName;
    var components = new Dictionary<string, ReleaseComponent>(StringComparer.Ordinal);
    foreach (var (name, id, argument) in programs)
    {
        var component = ReleaseManifest.ComponentDirectory(path, name);
        var identity = new string(id, 40);
        var version = $"{release ?? "dev"}+{identity}";
        var description = JsonSerializer.Serialize(new ProgramDescription(name, version, name == "host" ? null : [argument]));
        var executable = name == "host" ? HostBundle.Executable(component) : Path.Combine(component, ProgramDescription.ExecutableName(name, Platforms.CurrentRid));
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        if (name == "host")
        {
            File.Copy("/usr/bin/true", executable);
            File.Copy("/usr/bin/true", HostBundle.AppExecutable(component));
            var resources = Directory.CreateDirectory(Path.Combine(component, HostBundle.Name, "Contents", "Resources"));
            File.WriteAllText(Path.Combine(resources.FullName, "describe.json"), description);
            File.WriteAllText(Path.Combine(component, HostBundle.Name, "Contents", "Info.plist"), $$"""
                <plist version="1.0"><dict>
                <key>CFBundleIdentifier</key><string>{{instance.BundleIdentifier}}</string>
                </dict></plist>
                """);
        }
        else WriteExecutable(executable, $"#!/bin/sh\n[ \"$1\" = describe ] || exit 2\nprintf '%s\\n' {Quote(description)}\n");
        if (name == "browser")
        {
            var extension = Directory.CreateDirectory(Path.Combine(component, "extension"));
            File.WriteAllText(Path.Combine(extension.FullName, "manifest.json"), """{"name":"Browser"}""");
        }
        components.Add(name, new(identity, version, ReleaseManifest.HashFiles(component)));
    }
    new ReleaseManifest(release, instance.Name, Platforms.CurrentRid,
        new("0123456789abcdef0123456789abcdef01234567", checkout, "work", false), components)
        .Write(Path.Combine(path, ReleaseManifest.FileName));
    return path;
}

string Prepare(string name)
{
    var fixture = Directory.CreateDirectory(Under(name)).FullName;
    WriteExecutable(Path.Combine(fixture, "launchctl"), """
        #!/bin/sh
        set -eu
        root=$(/usr/bin/dirname "$0")
        case "$1" in
          print) [ -f "$root/agents/${2##*/}.plist.loaded" ] || exit 113; printf 'state = running\n' ;;
          bootout) /bin/rm "$root/agents/${2##*/}.plist.loaded"
                   printf 'bootout %s\n' "${2##*/}" >> "$root/launch-events" ;;
          bootstrap) label=${3##*/}
                     label=${label%.plist}
                     if [ -f "$root/fail-once" ] && [ "$(/bin/cat "$root/fail-once")" = "$label" ]; then /bin/rm "$root/fail-once"; exit 2; fi
                     /usr/bin/touch "$3.loaded"
                     printf 'bootstrap %s\n' "$label" >> "$root/launch-events" ;;
          kickstart) [ "$2" = '-k' ] && [ -f "$root/agents/${3##*/}.plist.loaded" ]
                     printf 'kickstart %s\n' "${3##*/}" >> "$root/launch-events" ;;
          *) exit 2 ;;
        esac
        """);
    WriteExecutable(Path.Combine(fixture, "codesign"), """
        #!/bin/sh
        set -eu
        case "$1" in
          --force) [ "$2" = --sign ] && [ "$3" = 'Orisumi Development' ] && [ -e "$5" ] ;;
          --verify) [ -f "$3/Contents/Info.plist" ] && [ ! -e "$3/invalid-signature" ] ;;
          --display)
            case "$2" in
              --verbose=4) identifier=$(/usr/bin/plutil -extract CFBundleIdentifier raw "$3/Contents/Info.plist")
                           printf 'Executable=%s/Contents/MacOS/OrisumiApp\nIdentifier=%s\nAuthority=Orisumi Development\n' "$3" "$identifier" >&2 ;;
              --extract-certificates=*) /bin/cp "$(/usr/bin/dirname "$0")/certificate.der" "${2#*=}0" ;;
              *) exit 2 ;;
            esac ;;
          *) exit 2 ;;
        esac
        """);
    return fixture;
}

string Under(string path) => Path.Combine(temp.FullName, path);

static HostBundle.Tools FixtureTools(string root, CommandProcess? run = null) =>
    new(Path.Combine(root, "codesign"), run ?? ReadHostDescription);

static async Task<int> ReadHostDescription(string executable, string[] arguments, TextWriter? output, CancellationToken token)
{
    Check(arguments is ["describe"], "fixture host only handles describe");
    var resources = Path.Combine(Path.GetDirectoryName(executable)!, "..", "Resources");
    await output!.WriteAsync(await File.ReadAllTextAsync(Path.Combine(resources, "describe.json"), token));
    return 0;
}

static SystemLocations Locations(string root) => new(Path.Combine(root, "agents"), Path.Combine(root, "commands"), Path.Combine(root, "launchctl"));

static Dictionary<string, XElement> PlistFields(string path) => XDocument.Load(path).Descendants("key")
    .ToDictionary(key => key.Value, key => key.ElementsAfterSelf().First());

static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

static void WriteExecutable(string path, string text)
{
    File.WriteAllText(path, text);
    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
}

static async Task ExpectAsync<T>(Func<Task> action, string? message = null) where T : Exception
{
    try { await action(); }
    catch (T error) when (message is null || error.Message.Contains(message)) { return; }
    throw new Exception($"Expected {typeof(T).Name} ({message})");
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
