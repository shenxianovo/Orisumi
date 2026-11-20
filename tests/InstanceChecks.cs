#:project ../src/Orisumi.Cli/Orisumi.Cli.csproj
#:project ../src/Orisumi.Dev/Orisumi.Dev.csproj
#:property AssemblyName=InstanceChecks
#:property PublishAot=false

using System.Runtime.Versioning;
using System.Xml.Linq;
using System.Text.Json;
using System.CommandLine;
using Orisumi.Cli;
using Orisumi.Cli.Capture;
using Orisumi.Dev;
using Orisumi.Platform;

[assembly: SupportedOSPlatform("macos")]

var checkout = Environment.CurrentDirectory;
var temp = Directory.CreateTempSubdirectory("orisumi-instance-check-");
try
{
    Resolution();
    await Install();
    await Deployment();
    Console.WriteLine("Instance, install and deploy checks passed.");
}
finally { temp.Delete(recursive: true); }

void Resolution()
{
    var root = Directory.CreateDirectory(Under("Checkout With-hyphen"));
    var output = root.CreateSubdirectory("src/project/bin");
    Expect<DirectoryNotFoundException>(() => Instance.Resolve(output.FullName));
    File.WriteAllText(Path.Combine(root.FullName, "Orisumi.slnx"), "<Solution />");
    var dev = Instance.Resolve(output.FullName);
    Check(dev.Name == "dev_checkout_with_hyphen" && dev.Store.Root == Path.Combine(root.FullName, ".local", "store"),
        "development name and checkout-local store");
    Check(Instance.Open(dev.Store.Root).Name == dev.Name, "first use creates instance.json");
    var other = Directory.CreateDirectory(Under("Other"));
    File.WriteAllText(Path.Combine(other.FullName, "Orisumi.slnx"), "<Solution />");
    Check(Instance.Resolve(other.FullName).Name == "dev_other", "another checkout gets a separate instance");

    var outer = Instance.Create(Under("outer"), "prod");
    var inner = Instance.Create(Path.Combine(outer.Store.Root, "nested"), "test_codex");
    var installed = Directory.CreateDirectory(Path.GetDirectoryName(inner.Store.HostExecutable)!);
    Check(Instance.Resolve(installed.FullName).Store.Root == inner.Store.Root, "nearest instance.json wins");
    Check(inner.CaptureAgentLabel("claude") == "dev.orisumi.test_codex.capture.claude" &&
        inner.AppAgentLabel == "dev.orisumi.test_codex.app" && inner.BrowserPort == 47913 && inner.CommandName == "orisumi-test_codex", "derived names");
    Check(outer.CaptureAgentLabel("codex") == "dev.orisumi.prod.capture.codex" &&
        outer.BrowserPort == 47810 && outer.CommandName == "orisumi", "production names");
    Check(ChromeExtension.DirectoryFor(dev, root.FullName) == DirectoryPaths.Resolve(Path.Combine(root.FullName, ".local", "extension")),
        "checkout instance uses its built extension");
    Check(Instance.Create(Under("console instance"), "console").AppAgentLabel == "dev.orisumi.console.app",
        "console is a valid instance name");
    var invalid = Directory.CreateDirectory(Under("invalid"));
    foreach (var name in new[] { "", "Prod", "test-prod", "../prod" })
    {
        File.WriteAllText(Path.Combine(invalid.FullName, "instance.json"), "{\"name\":\"" + name + "\"}");
        Expect<ArgumentException>(() => Instance.Resolve(invalid.FullName));
    }
    Check(ChromeExtension.Id("/Users/quq/Orisumi/src/Collectors/Orisumi.Collectors.Browser/ChromeExtension") ==
        "jbdbjgpholjhaneogagghkkgbailcehi", "known Chrome extension ID vector");
    Console.WriteLine("PASS: walk-up, checkout fallback, independent worktrees, missing and invalid instances, derived names and Chrome ID");
}

async Task Install()
{
    var instance = Instance.Create(Under("install store '中文"), "test_codex");
    var store = instance.Store;
    var dev = Instance.Development(Under("development"));
    var locations = new SystemLocations(Under("agents"), Under("commands"), Under("fake-launchctl"));
    WriteExecutable(locations.Launchctl, """
        #!/bin/sh
        set -eu
        case "$1" in
          bootstrap) touch "$3.loaded" ;;
          print) [ -f "$(dirname "$0")/agents/${2##*/}.plist.loaded" ] || exit 113 ;;
          bootout) rm "$(dirname "$0")/agents/${2##*/}.plist.loaded" ;;
          kickstart) [ "$2" = '-k' ] && [ -f "$(dirname "$0")/agents/${3##*/}.plist.loaded" ] || exit 2
                     printf '%s\n' "$3" >> "$(dirname "$0")/kickstarts" ;;
          *) exit 2 ;;
        esac
        """);
    using var output = new StringWriter();
    var host = await Package(Under("package from anywhere"), "host", "0.1.0+first");
    var browserRelease = await Package(Under("any browser directory"), "browser", "0.1.0+first");
    var originalManifest = Path.Combine(browserRelease, "extension", "manifest.json");
    var manifest = File.ReadAllText(originalManifest);
    await InstallCommand.Run(instance, [host, browserRelease], locations, output, default);
    await ExpectAsync<ArgumentException>(() => InstallCommand.Run(dev, [host], locations, output, default));
    var link = Path.Combine(locations.Commands, instance.CommandName);
    Check(new FileInfo(link).LinkTarget == store.HostExecutable &&
        Directory.GetFileSystemEntries(locations.Commands).Length == 1, "the installed CLI keeps its command link");

    var appLabel = instance.AppAgentLabel;
    var appPlist = Path.Combine(locations.Agents, appLabel + ".plist");
    var appFields = PlistFields(appPlist);
    Check(appFields["Label"].Value == appLabel && appFields["RunAtLoad"].Name == "true" &&
        !appFields.ContainsKey("KeepAlive") && appFields["ProgramArguments"].Elements().Select(element => element.Value)
            .SequenceEqual([HostBundle.AppExecutable(store.Host), "--menu-bar"]) &&
        IsAssociated(appFields) && File.Exists(appPlist + ".loaded"), "the app launches at login under Orisumi and stays quit");
    Check(Directory.GetFiles(locations.Agents, "*.plist").Length == 1 && !Directory.Exists(dev.Store.Host),
        "dev uses the checkout host and installs no app agent");
    var extension = Path.Combine(store.Collector("browser"), "extension");
    Check(File.ReadAllText(Path.Combine(extension, "manifest.json")).Contains("Orisumi Chrome Collector (test_codex)"),
        "installed manifest identifies the instance");
    ChromeExtension.CheckConfiguration(extension, instance);
    Check(File.ReadAllText(originalManifest) == manifest && !File.Exists(Path.Combine(browserRelease, "extension", "config.json")),
        "release extension remains unconfigured");

    var next = await Package(Under("new host"), "host", "0.1.0+next");
    File.WriteAllText(Path.Combine(store.Host, "old-only"), "old");
    File.Delete(link);
    File.CreateSymbolicLink(link, Path.Combine(temp.FullName, "obsolete"));
    await InstallCommand.Run(instance, [next], locations, output, default);
    Check((await ProgramDescription.Read(File.ResolveLinkTarget(link, true)!.FullName, [], default)).Version == "0.1.0+next",
        "the refreshed link points to the replacement host");
    Check(!File.Exists(Path.Combine(store.Host, "old-only")), "the old install is gone");
    Check(File.ReadAllLines(Under("kickstarts")).SequenceEqual([Launchd.Service(appLabel)]),
        "reinstall kickstarts the loaded app");
    Check(!Directory.EnumerateDirectories(store.App).Any(path => Path.GetFileName(path).StartsWith("host.", StringComparison.Ordinal)),
        "no staged host directory remains");

    var bad = await Package(Under("bad browser"), "browser", "0.1.0+bad");
    File.WriteAllText(Path.Combine(bad, "extension", "manifest.json"), "malformed");
    await ExpectAsync<JsonException>(() => InstallCommand.Run(instance, [bad], locations, output, default));
    var wrong = await Package(Under("wrong browser"), "browser", "0.1.0+wrong");
    File.WriteAllText(Path.Combine(wrong, "name"), "codex");
    await ExpectAsync<InvalidDataException>(() => InstallCommand.Run(instance, [wrong], locations, output, default));
    var unverified = await Package(Under("failing describe"), "browser", "0.1.0+unverified");
    File.WriteAllText(Path.Combine(unverified, "orisumi-browser"), "#!/bin/sh\nexit 3\n");
    await ExpectAsync<IOException>(() => InstallCommand.Run(instance, [unverified], locations, output, default));
    Check((await ProgramDescription.Read(Path.Combine(store.Collector("browser"), "orisumi-browser"), [], default)).Version == "0.1.0+first" &&
        !Directory.GetDirectories(store.Collectors).Any(path => Path.GetFileName(path).StartsWith("browser.", StringComparison.Ordinal)), "invalid releases leave the installed version intact");

    await HostBundle.Verify(store.Host, default);
    Check(Instance.Resolve(Path.GetDirectoryName(store.HostExecutable)!).Store.Root == store.Root, "bundle walks up to instance.json");
    var portConfig = Path.Combine(extension, "config.json");
    var config = File.ReadAllText(portConfig);
    File.WriteAllText(portConfig, $$"""{"instance":"{{instance.Name}}","port":47810}""");
    Expect<InvalidDataException>(() => ChromeExtension.CheckConfiguration(extension, instance));
    File.WriteAllText(portConfig, config);

    var unsigned = await Package(Under("unsigned host"), "host", "0.1.0+unsigned", authority: null);
    var adHoc = await Package(Under("adhoc host"), "host", "0.1.0+adhoc", authority: "-");
    var tampered = await Package(Under("tampered host"), "host", "0.1.0+tampered");
    File.AppendAllText(Path.Combine(tampered, HostBundle.Name, "Contents/Resources/version"), "tampered");
    foreach (var invalid in new[] { unsigned, adHoc, tampered })
        await ExpectAsync<InvalidDataException>(() => InstallCommand.Run(instance, [invalid], locations, output, default));
    Check((await ProgramDescription.Read(store.HostExecutable, [], default)).Version == "0.1.0+next",
        "signature failures leave the installed bundle intact");

    var symlink = Under("aliased store");
    Directory.CreateSymbolicLink(symlink, store.Root);
    var alias = Instance.Open(symlink);
    await CaptureStartup.Start(alias, locations, output, default, baseDirectory: store.Host);
    var plist = Path.Combine(locations.Agents, alias.CaptureAgentLabel("browser") + ".plist");
    var collectorFields = PlistFields(plist);
    var arguments = collectorFields["ProgramArguments"].Elements().Select(element => element.Value);
    Check(collectorFields["Label"].Value == "dev.orisumi.test_codex.capture.browser" &&
        IsAssociated(collectorFields) && arguments.SequenceEqual([alias.Store.HostExecutable, "launch", "--",
        Path.Combine(alias.Store.Collector("browser"), "orisumi-browser"), "--store", alias.Store.Root, "capture"]),
        "the collector starts under the bundle and is associated with Orisumi");
    Check(output.ToString().Contains($"Load unpacked Chrome extension: {DirectoryPaths.Resolve(extension)}") &&
        output.ToString().Contains($"Expected Chrome extension ID: {ChromeExtension.Id(DirectoryPaths.Resolve(extension))}"),
        "start reports the extension's real path and ID");
    var other = Instance.Create(Under("other store"), "other");
    var otherAgent = Path.Combine(locations.Agents, other.CaptureAgentLabel("browser") + ".plist");
    File.WriteAllText(otherAgent, "preserve");
    await CaptureStartup.Stop(alias, locations, output, default);
    Check(!File.Exists(plist) && !File.Exists(plist + ".loaded") && File.Exists(otherAgent) &&
        File.Exists(appPlist) && File.Exists(appPlist + ".loaded"), "capture stop leaves the app loaded");
    Console.WriteLine("PASS: signed bundle install, atomic replacement, command link, signature refusals, extension configuration, app bootstrap and kickstart, and isolated capture stop");
}

async Task Deployment()
{
    var source = Under("deploy checkout");
    Check(await RunProcess.Run("git", ["clone", "-q", "--shared", checkout, source], null, default) == 0, "clone deploy fixture");
    using var revision = new StringWriter();
    Check(await RunProcess.Run("git", ["rev-parse", "HEAD"], revision, default, directory: source) == 0, "read fixture HEAD");
    var version = "0.1.0+" + revision.ToString().Trim();
    string[] collectors = ["browser", "claude", "codex", "macos"];
    foreach (var component in new[] { "host" }.Concat(collectors))
        await Package(Under("packages/" + component), component, version);

    WriteExecutable(Under("swift"), """
        #!/bin/sh
        set -eu
        [ "$1 $2 $3 $4 $5 $6 $7 $8" = 'build -c release --package-path src/Orisumi.App.MacOS --product OrisumiApp --scratch-path' ]
        if [ "${10}" = --show-bin-path ]; then printf '%s\n' "$9/release"; exit; fi
        [ "${10} ${11}" = '-Xswiftc -warnings-as-errors' ]
        fixtures=$(dirname "$0")
        mkdir -p "$9/release"
        cp "$fixtures/packages/host/Orisumi.app/Contents/MacOS/OrisumiApp" "$9/release/OrisumiApp"
        """);

    var dotnet = Under("fake-dotnet");
    WriteExecutable(dotnet, """
        #!/bin/sh
        set -eu
        case "$1" in
          msbuild)
            [ "$#" = 4 ] && [ "$3" = -nologo ] && [ "$4" = -getProperty:Version ] || exit 2
            printf '0.1.0\n'
            exit ;;
          publish) ;;
          *) exit 2 ;;
        esac
        [ "$#" = 12 ] && [ "$3 $4 $5 $6 $7" = "-c Release -r osx-arm64 -p:PublishAot=true" ] || exit 2
        [ "$8" = "-p:SourceRevisionId=$(git rev-parse HEAD)" ]
        [ "$9 ${10} ${11}" = "-warnaserror -tl:off -o" ]
        name=${2##*/}
        case "$name" in
          Orisumi.Cli.csproj) component=host ;;
          Orisumi.Collectors.*.csproj)
            name=${name#Orisumi.Collectors.}
            component=$(printf '%s' "${name%.csproj}" | tr '[:upper:]' '[:lower:]') ;;
          *) exit 2 ;;
        esac
        out=${12}
        fixtures=$(dirname "$0")
        mkdir -p "$out"
        directory=$out
        if [ "$component" = host ]; then
          directory=${out%/Orisumi.app/Contents/MacOS}
        fi
        printf '%s\n' "$directory" >> "$fixtures/published"
        if [ -f "$fixtures/fail-publish" ] && [ "$(cat "$fixtures/fail-publish")" = "$component" ]; then
          exit 17
        fi
        package="$fixtures/packages/$component"
        if [ "$component" = host ]; then
          cp -R "$package/Orisumi.app/Contents/Resources" "$out/.."
          cp "$package/Orisumi.app/Contents/MacOS/orisumi" "$package/Orisumi.app/Contents/MacOS/libe_sqlite3.dylib" "$out/"
        else
          cp "$package/name" "$package/version" "$package/orisumi-$component" "$out/"
        fi
        """);
    WriteExecutable(Under("npm"), """
        #!/bin/sh
        set -eu
        [ -f manifest.json ]
        if [ "$*" = ci ]; then exit; fi
        [ "$#" = 5 ] && [ "$1 $2 $3 $4" = "run build -- --outDir" ] || exit 2
        mkdir -p "$5"
        """);

    var root = new RootCommand();
    Deploy.AddTo(root, dotnet);
    var prod = new Store(Under("prod '中文"));
    var all = new Store(Under("all"));
    var hostOnly = new Store(Under("host only"));
    var events = Under("events");
    var published = Under("published");
    var path = Environment.GetEnvironmentVariable("PATH");
    try
    {
        Environment.CurrentDirectory = source;
        Environment.SetEnvironmentVariable("PATH", temp.FullName + Path.PathSeparator + path);
        var untracked = Path.Combine(source, "untracked");
        File.WriteAllText(untracked, "dirty");
        await ExpectAsync<IOException>(() => Invoke("deploy", "--prod", prod.Root, "claude"), "干净");
        Check(!Directory.Exists(prod.Root) && !File.Exists(published), "dirty worktree refuses before publishing or creating prod");
        File.Delete(untracked);

        Check(await Invoke("deploy", "--prod", prod.Root, "claude") == 0, "deploy selected collector");
        var first = CheckCleanup("host", "claude");
        Check(Instance.Open(prod.Root).Name == "prod" &&
            (await ProgramDescription.Read(prod.HostExecutable, [], default)).Version == version &&
            File.ReadAllText(Path.Combine(prod.Collector("claude"), "version")) == version &&
            Directory.GetDirectories(prod.Collectors).Length == 1, "collector selection also installs the signed host");
        await CheckHost(prod);
        await CheckAppIcon(prod);
        Check(File.ReadAllLines(events).SequenceEqual(["install", "start"]), "temporary host installs before installed host starts");

        Check(await Invoke("deploy", "--prod", all.Root) == 0, "deploy all components");
        var second = CheckCleanup(["host", .. collectors]);
        Check(second != first && collectors.All(component => Directory.Exists(all.Collector(component))) &&
            File.Exists(Path.Combine(all.Collector("browser"), "extension", "manifest.json")),
            "fresh publish builds all collectors and the extension");
        await CheckHost(all);
        Check(File.ReadAllLines(events).SequenceEqual(["install", "start", "install", "start"]), "all components install before capture starts");

        await ExpectAsync<ArgumentException>(() => Invoke("deploy", "--prod", hostOnly.Root, "console"), "未知的部署组件");
        Check(await Invoke("deploy", "--prod", hostOnly.Root, "host") == 0, "deploy host component");
        CheckCleanup("host");
        await CheckHost(hostOnly);
        Check(!Directory.Exists(hostOnly.Collectors), "host selection only installs the app bundle");
        var expectedEvents = new[] { "install", "start", "install", "start", "install", "start" };
        Check(File.ReadAllLines(events).SequenceEqual(expectedEvents), "host installs before capture starts");

        var sentinel = Path.Combine(prod.Host, "existing");
        File.WriteAllText(sentinel, "keep");
        File.WriteAllText(Under("fail-publish"), "codex");
        await ExpectAsync<IOException>(() => Invoke("deploy", "--prod", prod.Root, "codex"), "codex: AOT publish exited with code 17");
        var failed = CheckCleanup("host", "codex");
        Check(failed != first && failed != second && File.ReadAllText(sentinel) == "keep" &&
            !Directory.Exists(prod.Collector("codex")) && File.ReadAllLines(events).SequenceEqual(expectedEvents),
            "publish failure leaves the existing production install and capture untouched");
        Console.WriteLine("PASS: deploy host with signed GUI, CLI and SQLite, component selection, dirty-tree refusal, install/start order, temporary cleanup and publish failure isolation");
    }
    finally
    {
        Environment.CurrentDirectory = checkout;
        Environment.SetEnvironmentVariable("PATH", path);
    }

    async Task CheckHost(Store store)
    {
        await HostBundle.Verify(store.Host, default);
        var fields = PlistFields(Path.Combine(store.Host, HostBundle.Name, "Contents", "Info.plist"));
        Check(fields["CFBundleExecutable"].Value == HostBundle.AppExecutableName &&
            fields["CFBundleDisplayName"].Value == "Orisumi" && fields["CFBundleVersion"].Value == version &&
            fields["LSUIElement"].Name == "true", "the GUI is the signed agent-style app's main executable");
        foreach (var executable in new[] { store.HostExecutable, Path.Combine(Path.GetDirectoryName(store.HostExecutable)!, "libe_sqlite3.dylib") })
        {
            using var signature = new StringWriter();
            Check(await RunProcess.Run(HostBundle.Codesign, ["--display", "--verbose=4", executable], TextWriter.Null,
                default, errors: signature) == 0 && signature.ToString().Split('\n').Contains($"Authority={HostBundle.Authority}"),
                $"the bundle also contains a signed {Path.GetFileName(executable)}");
        }
    }

    async Task CheckAppIcon(Store store)
    {
        var contents = Path.Combine(store.Host, HostBundle.Name, "Contents");
        var fields = PlistFields(Path.Combine(contents, "Info.plist"));
        Check(fields["CFBundleIconFile"].Value == "AppIcon.icns", "Info.plist points to the bundled app icon");
        var iconset = Under("deployed.iconset");
        var icon = Path.Combine(contents, "Resources", "AppIcon.icns");
        Check(await RunProcess.Run("/usr/bin/iconutil", ["-c", "iconset", icon, "-o", iconset], null, default) == 0,
            "extract the signed bundle's icon");
        foreach (var points in new[] { 16, 32, 128, 256, 512 })
        {
            foreach (var scale in new[] { 1, 2 })
            {
                var name = $"icon_{points}x{points}{(scale == 2 ? "@2x" : "")}.png";
                using var dimensions = new StringWriter();
                Check(await RunProcess.Run("/usr/bin/sips",
                    ["-g", "pixelWidth", "-g", "pixelHeight", Path.Combine(iconset, name)], dimensions, default) == 0 &&
                    dimensions.ToString().Split('\n').Select(line => line.Trim()).Intersect(
                        [$"pixelWidth: {points * scale}", $"pixelHeight: {points * scale}"]).Count() == 2,
                    $"deployed icon includes {name} at {points * scale} pixels square");
            }
        }
        Console.WriteLine("PASS: signed host app icon, CFBundleIconFile and all ten standard 1x/2x icon sizes");
    }

    Task<int> Invoke(params string[] arguments) => root.Parse(arguments)
        .InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = false });

    string CheckCleanup(params string[] components)
    {
        var directories = File.ReadAllLines(published);
        File.Delete(published);
        Check(directories.Select(Path.GetFileName).SequenceEqual(components), "publish order and components");
        var staging = Path.GetDirectoryName(directories[0])!;
        Check(directories.All(directory => Path.GetDirectoryName(directory) == staging) &&
            !Directory.Exists(staging), "deployment removes the whole temporary directory");
        return staging;
    }
}

string Under(string path) => Path.Combine(temp.FullName, path);

async Task<string> Package(string path, string name, string version, string? authority = HostBundle.Authority)
{
    Directory.CreateDirectory(path);
    var host = name == "host";
    var resources = host ? Path.Combine(path, HostBundle.Name, "Contents", "Resources") : path;
    Directory.CreateDirectory(resources);
    File.WriteAllText(Path.Combine(resources, "name"), name);
    File.WriteAllText(Path.Combine(resources, "version"), version);
    if (name == "browser")
    {
        var extension = Directory.CreateDirectory(Path.Combine(path, "extension"));
        File.WriteAllText(Path.Combine(extension.FullName, "manifest.json"), """{"name":"Orisumi Chrome Collector"}""");
    }
    var executable = Path.Combine(path, ProgramDescription.ExecutableName(name));
    if (host)
    {
        // A Mach-O entry point runs the sealed fixture script. Every operation stays inside the test directories.
        var fixture = Under("host-fixture");
        if (!File.Exists(fixture))
        {
            var source = fixture + ".c";
            File.WriteAllText(source, """
                #include <mach-o/dyld.h>
                #include <limits.h>
                #include <string.h>
                #include <unistd.h>
                int main(int argc, char **argv) {
                    char path[PATH_MAX];
                    uint32_t size = sizeof(path);
                    if (_NSGetExecutablePath(path, &size) != 0) return 90;
                    char *filename = strrchr(path, '/') + 1;
                    const char script[] = "../Resources/fixture";
                    if (filename - path + sizeof(script) > sizeof(path)) return 91;
                    memcpy(filename, script, sizeof(script));
                    argv[0] = path;
                    execv(path, argv);
                    return 92;
                }
                """);
            Check(await RunProcess.Run("/usr/bin/clang", [source, "-o", fixture], null, default) == 0, "build host fixture");
        }
        executable = HostBundle.Executable(path);
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.Copy(fixture, executable);
        File.Copy("/usr/bin/true", HostBundle.AppExecutable(path));
        var library = Under("sqlite-fixture.dylib");
        if (!File.Exists(library))
            Check(await RunProcess.Run("/usr/bin/clang", ["-dynamiclib", "-x", "c", "/dev/null", "-o", library],
                null, default) == 0, "build SQLite library signature fixture");
        File.Copy(library, Path.Combine(Path.GetDirectoryName(executable)!, "libe_sqlite3.dylib"));
        var identifier = authority == HostBundle.Authority ? HostBundle.Identifier : "dev.orisumi.test";
        File.WriteAllText(Path.Combine(path, HostBundle.Name, "Contents", "Info.plist"), $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict>
            <key>CFBundleIdentifier</key><string>{{identifier}}</string>
            <key>CFBundleExecutable</key><string>{{HostBundle.AppExecutableName}}</string>
            <key>CFBundlePackageType</key><string>APPL</string>
            </dict></plist>
            """);
    }

    WriteExecutable(host ? Path.Combine(resources, "fixture") : executable, """
        #!/bin/sh
        set -eu
        dir=$(dirname "$0")
        name=$(cat "$dir/name")
        if [ "$1" = describe ]; then
          version=$(cat "$dir/version")
          if [ "$name" = host ]; then
            printf '{"name":"host","version":"%s"}\n' "$version"
          else
            printf '{"name":"%s","version":"%s","arguments":["capture"]}\n' "$name" "$version"
          fi
          exit
        fi
        [ "$name" = host ] && [ "$1" = --store ] && [ -f "$2/instance.json" ] || exit 2
        store=$2
        shift 2
        events="$(dirname "$store")/events"
        if [ "$1" = capture ]; then
          [ "$#" = 2 ] && [ "$2" = start ] && [ "$dir" -ef "$store/app/host/Orisumi.app/Contents/Resources" ] || exit 2
          printf 'start\n' >> "$events"
          exit
        fi
        [ "$1" = install ] && [ "$dir" -ef "$2/Orisumi.app/Contents/Resources" ] || exit 2
        shift
        printf 'install\n' >> "$events"
        for package; do
          if [ -d "$package/Orisumi.app" ]; then
            destination="$store/app/host"
          else
            name=$(cat "$package/name")
            destination="$store/app/collectors/$name"
          fi
          mkdir -p "$destination"
          cp -R "$package/." "$destination/"
        done
        """);
    if (host)
    {
        string[] signing = authority is null
            ? ["--remove-signature", HostBundle.AppExecutable(path)]
            : ["--force", "--sign", authority, "--timestamp=none", Path.Combine(path, HostBundle.Name)];
        Check(await RunProcess.Run(HostBundle.Codesign, signing, null, default) == 0, "prepare bundle signature fixture");
    }
    return path;
}

static Dictionary<string, XElement> PlistFields(string path) => XDocument.Load(path).Descendants("key")
    .ToDictionary(key => key.Value, key => key.ElementsAfterSelf().First());

static bool IsAssociated(Dictionary<string, XElement> fields) =>
    fields["AssociatedBundleIdentifiers"].Elements().Select(element => element.Value).SequenceEqual([HostBundle.Identifier]);

static void WriteExecutable(string path, string content)
{
    File.WriteAllText(path, content);
    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
}

static void Expect<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
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
