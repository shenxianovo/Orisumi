#:project ../src/Orisumi.Cli/Orisumi.Cli.csproj
#:project ../src/Orisumi.Dev/Orisumi.Dev.csproj
#:property AssemblyName=InstanceChecks
#:property PublishAot=false

using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
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
    await Workflow();
    Console.WriteLine("Instance, install and release workflow checks passed.");
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
    var installed = Directory.CreateDirectory(Path.Combine(inner.Store.Host, "runtime"));
    Check(Instance.Resolve(installed.FullName).Store.Root == inner.Store.Root, "nearest instance.json wins");
    Check(inner.AgentLabel("claude") == "dev.orisumi.test_codex.claude" &&
        inner.ChromeHost == "com.orisumi.test_codex.browser" && inner.CommandName == "orisumi-test_codex", "derived names");
    Check(outer.AgentLabel("codex") == "dev.orisumi.prod.codex" &&
        outer.ChromeHost == "com.orisumi.prod.browser" && outer.CommandName == "orisumi", "production names");
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
    var locations = new SystemLocations(Under("agents"), Under("manifests"), Under("commands"), "/usr/bin/false");
    using var output = new StringWriter();
    var host = Package(Under("package from anywhere"), "host", "0.1.0+first");
    var browserRelease = Package(Under("any browser directory"), "browser", "0.1.0+first");
    var originalManifest = Path.Combine(browserRelease, "extension", "manifest.json");
    var manifest = File.ReadAllText(originalManifest);
    await InstallCommand.Run(instance, [host, browserRelease], locations, output, default);
    await InstallCommand.Run(dev, [host], locations, output, default);
    var link = Path.Combine(locations.Commands, instance.CommandName);
    Check(new FileInfo(link).LinkTarget == Path.Combine(store.Host, "orisumi") &&
        Directory.GetFileSystemEntries(locations.Commands).Length == 1, "only the installed instance gets a link");
    var extension = Path.Combine(store.Collector("browser"), "extension");
    Check(File.ReadAllText(Path.Combine(extension, "manifest.json")).Contains("Orisumi Chrome Collector (test_codex)"),
        "installed manifest identifies the instance");
    ChromeExtension.CheckConfiguration(extension, instance);
    Check(File.ReadAllText(originalManifest) == manifest && !File.Exists(Path.Combine(browserRelease, "extension", "config.json")),
        "release extension remains unconfigured");

    var next = Package(Under("new host"), "host", "0.1.0+next");
    File.WriteAllText(Path.Combine(store.Host, "old-only"), "old");
    File.Delete(link);
    File.CreateSymbolicLink(link, Path.Combine(temp.FullName, "obsolete"));
    await InstallCommand.Run(instance, [next], locations, output, default);
    Check((await ProgramDescription.Read(File.ResolveLinkTarget(link, true)!.FullName, [], default)).Version == "0.1.0+next",
        "the refreshed link points to the replacement host");
    Check(!File.Exists(Path.Combine(store.Host, "old-only")), "the old install is gone");
    Check(!Directory.EnumerateDirectories(store.App).Any(path => Path.GetFileName(path).StartsWith("host.")),
        "no staged host directory remains");

    var bad = Package(Under("bad browser"), "browser", "0.1.0+bad");
    File.WriteAllText(Path.Combine(bad, "extension", "manifest.json"), "malformed");
    await ExpectAsync<JsonException>(() => InstallCommand.Run(instance, [bad], locations, output, default));
    var wrong = Package(Under("wrong browser"), "browser", "0.1.0+wrong");
    File.WriteAllText(Path.Combine(wrong, "name"), "codex");
    await ExpectAsync<InvalidDataException>(() => InstallCommand.Run(instance, [wrong], locations, output, default));
    var unverified = Package(Under("failing describe"), "browser", "0.1.0+unverified");
    File.WriteAllText(Path.Combine(unverified, "orisumi-browser"), "#!/bin/sh\nexit 3\n");
    await ExpectAsync<IOException>(() => InstallCommand.Run(instance, [unverified], locations, output, default));
    Check((await ProgramDescription.Read(Path.Combine(store.Collector("browser"), "orisumi-browser"), [], default)).Version == "0.1.0+first" &&
        !Directory.EnumerateDirectories(store.Collectors).Any(path => Path.GetFileName(path).StartsWith("browser.")), "invalid releases leave the installed version intact");

    var symlink = Under("aliased store");
    Directory.CreateSymbolicLink(symlink, store.Root);
    var alias = Instance.Open(symlink);
    await CaptureStartup.Start(alias, locations, output, default, baseDirectory: store.Host);
    var registered = ChromeExtension.Manifest(instance, locations);
    using (var actual = JsonDocument.Parse(File.ReadAllText(registered)))
        Check(actual.RootElement.GetProperty("allowed_origins")[0].GetString() ==
            $"chrome-extension://{ChromeExtension.Id(DirectoryPaths.Resolve(extension))}/", "registration uses real extension path");
    var launcher = File.ReadAllText(store.ChromeLauncher);
    Check(launcher.Contains("'/") && launcher.Contains("/orisumi-browser'") && !launcher.Contains("dotnet"),
        "installed browser launches directly");
    var otherManifest = Path.Combine(locations.ChromeManifests, "com.orisumi.other.browser.json");
    File.WriteAllText(otherManifest, "preserve");
    await CaptureStartup.Stop(alias, locations, output, default);
    Check(!File.Exists(registered) && !File.Exists(store.ChromeLauncher) && File.Exists(otherManifest),
        "stop removes only this instance's browser registration");
    Console.WriteLine("PASS: describe verification, atomic replacement, command links, extension customisation, real-path registration and isolated cleanup");
}

async Task Workflow()
{
    var releases = Under("releases");
    var preview = new Store(Under("preview"));
    var prod = new Store(Under("prod"));
    var head = await Releases.Head(checkout, default);
    var latest = "0.1.0+" + head;
    var candidate = "0.1.0+candidate";
    var dirty = Directory.CreateDirectory(Under("dirty"));
    Check(await RunProcess.Run("git", ["init", "-q"], null, default, directory: dirty.FullName) == 0, "init fixture");
    File.WriteAllText(Path.Combine(dirty.FullName, "untracked"), "dirty");
    await ExpectAsync<IOException>(() => Releases.Build(dirty.FullName, releases, ["host"], TextWriter.Null, default), "干净");
    Check(!Directory.Exists(releases), "dirty release writes nothing");
    await ExpectAsync<DirectoryNotFoundException>(() => InstanceWorkflow.Preview(checkout, releases, preview, ["claude"], default));
    Check(!Directory.Exists(preview.Root), "missing release does not create preview");

    foreach (var component in new[] { "host", "claude", "codex" })
        Package(Path.Combine(releases, component, latest), component, latest);
    await InstanceWorkflow.Preview(checkout, releases, preview, ["claude"], default);
    Check(Instance.Open(preview.Root).Name == "preview" &&
        File.ReadAllText(Path.Combine(preview.Host, "version")) == latest &&
        File.ReadAllText(Path.Combine(preview.Collector("claude"), "version")) == latest &&
        !Directory.Exists(preview.Collector("codex")), "preview uses HEAD host and selected collectors");

    Package(Path.Combine(releases, "claude", candidate), "claude", candidate);
    File.WriteAllText(Path.Combine(preview.Collector("claude"), "version"), candidate);
    var migration = Under("migration file.cs");
    File.WriteAllText(migration, """
        var root = args.Single();
        File.AppendAllText(Path.Combine(Path.GetDirectoryName(root)!, "events"), "prod migrate\n");
        """);
    var events = Under("events");
    File.WriteAllText(events, "");
    Instance.Create(prod.Root, "prod");
    Directory.CreateDirectory(prod.App);
    Package(prod.Host, "host", "0.1.0+older");
    await InstanceWorkflow.Promote(checkout, releases, prod, preview, ["claude", "codex"], migration, default);
    Check(File.ReadAllText(Path.Combine(prod.Collector("claude"), "version")) == candidate &&
        File.ReadAllText(Path.Combine(prod.Collector("codex"), "version")) == latest, "promote uses exact preview version and HEAD fallback");
    Check(File.ReadAllLines(events).SequenceEqual(["prod stop", "prod migrate", "prod install", "prod start", "preview stop"]),
        "stop, migrate, install, start and stop preview ordering");
    Check(File.ReadAllText(Path.Combine(releases, "claude", candidate, "version")) == candidate, "releases remain unchanged");

    File.WriteAllText(migration, "return 17;");
    File.WriteAllText(events, "");
    await ExpectAsync<IOException>(() => InstanceWorkflow.Promote(checkout, releases, prod, preview, ["claude"], migration, default),
        "migration");
    Check(File.ReadAllLines(events).SequenceEqual(["prod stop"]), "migration failure prevents install and further capture changes");

    var first = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds();
    WriteRuns("first", first, "old", 111, [first, first - 3_600_001]);
    WriteRuns("second", first + 1, candidate, 222, [first + 1]);
    using var report = new StringWriter();
    await InstanceStatus.Write("prod", prod, report, default);
    Check(report.ToString().Contains($"claude {candidate}") &&
        report.ToString().Contains($"collector_version={candidate} confirmed_until_utc_ms=222 read_failures_last_hour=2"),
        "status uses the latest run across databases and counts only recent failures");
    Console.WriteLine("PASS: dirty tree refusal, missing release, preview, exact promotion with migration and fallback, failure ordering and status");

    void WriteRuns(string name, long started, string version, long confirmed, long[] failures)
    {
        Directory.CreateDirectory(prod.Observations("claude"));
        using var db = new SqliteConnection($"Data Source={Path.Combine(prod.Observations("claude"), name + ".sqlite")};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE capture_runs (run_id INTEGER PRIMARY KEY, started_at_utc_ms, collector_version, confirmed_until_utc_ms);
            CREATE TABLE capture_read_failures (observed_at_utc_ms);
            INSERT INTO capture_runs VALUES (1, $started, $version, $confirmed);
            """;
        command.Parameters.AddWithValue("$started", started);
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$confirmed", confirmed);
        command.ExecuteNonQuery();
        command.Parameters.Clear();
        command.CommandText = "INSERT INTO capture_read_failures VALUES ($time)";
        var parameter = command.Parameters.AddWithValue("$time", 0L);
        foreach (var failure in failures) { parameter.Value = failure; command.ExecuteNonQuery(); }
    }
}

string Under(string path) => Path.Combine(temp.FullName, path);

static string Package(string path, string name, string version)
{
    Directory.CreateDirectory(path);
    File.WriteAllText(Path.Combine(path, "name"), name);
    File.WriteAllText(Path.Combine(path, "version"), version);
    if (name == "browser")
    {
        var extension = Directory.CreateDirectory(Path.Combine(path, "extension"));
        File.WriteAllText(Path.Combine(extension.FullName, "manifest.json"), """{"name":"Orisumi Chrome Collector"}""");
    }
    var executable = Path.Combine(path, ProgramDescription.ExecutableName(name));
    File.WriteAllText(executable, """
        #!/bin/sh
        set -eu
        dir=$(dirname "$0")
        name=$(cat "$dir/name")
        if [ "$1" = describe ]; then
          version=$(cat "$dir/version")
          if [ "$name" = host ]; then
            printf '{"name":"host","version":"%s"}\n' "$version"
          else
            printf '{"name":"%s","version":"%s","start":{"mode":"chrome","arguments":[]}}\n' "$name" "$version"
          fi
          exit
        fi
        [ "$name" = host ] && [ "$1" = --store ]
        store=$2
        shift 2
        events="$(dirname "$store")/events"
        if [ "$1" = capture ]; then
          printf '%s %s\n' "${store##*/}" "$2" >> "$events"
          exit
        fi
        [ "$1" = install ]
        shift
        printf '%s install\n' "${store##*/}" >> "$events"
        for release; do
          name=$(cat "$release/name")
          destination="$store/app/collectors/$name"
          if [ "$name" = host ]; then destination="$store/app/host"; fi
          mkdir -p "$destination"
          cp -R "$release/." "$destination/"
        done
        """);
    File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    return path;
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
