#:project ../Host/Cli/Orisumi.Cli.csproj
#:project ../Tools/Dev/Orisumi.Dev.csproj
#:property AssemblyName=InstanceChecks
#:property PublishAot=false

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Orisumi.Cli;
using Orisumi.Cli.Capture;
using Orisumi.Dev;
using Orisumi.Desktop;
using Orisumi.Platform;

[assembly: SupportedOSPlatform("macos")]

var checkout = Environment.CurrentDirectory;
var temp = Directory.CreateTempSubdirectory("orisumi-instance-check-");
try
{
    Resolution();
    var source = await Identity();
    await Install();
    await RollbackChangedCollectors();
    await Deployment(source);
    await Update();
    await ForwardingAndReset();
    Console.WriteLine("Instance, identity, install, deploy, release and update checks passed.");
    return 0;
}
finally { temp.Delete(recursive: true); }

void Resolution()
{
    var root = Directory.CreateDirectory(Under("checkout"));
    var output = root.CreateSubdirectory("src/project/bin");
    File.WriteAllText(Path.Combine(root.FullName, "Orisumi.slnx"), "<Solution />");
    Expect<DirectoryNotFoundException>(() => Instance.Resolve(output.FullName));
    var prod = Instance.Create(Under("outer"), "prod");
    var dev = Instance.Create(Path.Combine(prod.Store.Root, "nested"), "dev");
    var installed = Directory.CreateDirectory(Path.GetDirectoryName(dev.Store.HostExecutable)!);
    Check(Instance.Resolve(installed.FullName).Store.Root == dev.Store.Root, "nearest instance.json wins");
    Check(prod.CaptureAgentLabel("codex") == "dev.orisumi.prod.capture.codex" &&
        prod.BrowserPort == 47810 && prod.BundleIdentifier == "dev.orisumi" && prod.CommandName == "orisumi", "prod names");
    Check(dev.CaptureAgentLabel("browser") == "dev.orisumi.dev.capture.browser" &&
        dev.AppAgentLabel == "dev.orisumi.dev.app" && dev.BrowserPort == 47811 &&
        dev.BundleIdentifier == "dev.orisumi.canary" && dev.CommandName is null, "dev names");
    var other = Instance.Create(Under("other"), "test_codex");
    Check(other.BrowserPort == 47913 && other.CommandName is null && other.BundleIdentifier == dev.BundleIdentifier, "test instance names");
    Check(Instance.ProductionRoot().EndsWith("/Library/Application Support/Orisumi") &&
        Instance.DevelopmentRoot().EndsWith("/Library/Application Support/Orisumi-dev"), "default roots");
    Expect<ArgumentException>(() => Instance.Create(Under("invalid"), "../prod"));
    Check(ChromeExtension.Id("/Users/quq/Orisumi/src/Collectors/Orisumi.Collectors.Browser/ChromeExtension") ==
        "jbdbjgpholjhaneogagghkkgbailcehi", "known Chrome extension ID vector");
    Console.WriteLine("PASS: instance walk-up, default roots, bundle IDs, ports and command links");
}

async Task<string> Identity()
{
    var source = Directory.CreateDirectory(Under("fixture checkout")).FullName;
    const string browser = "Collectors/Browser/Orisumi.Collectors.Browser.csproj";
    const string project = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>""";
    const string props = """
        <Project><ItemGroup>
          <ProjectReference Include="$(MSBuildThisFileDirectory)../Shared/Capture/Capture.csproj" />
        </ItemGroup></Project>
        """;
    Write("Directory.Build.props", "<Project />");
    Write("Collectors/Directory.Build.props", props);
    foreach (var path in new[] { Repository.CliProject, browser, "Collectors/Codex/Orisumi.Collectors.Codex.csproj",
        "Shared/Capture/Capture.csproj", "Shared/Platform/Platform.csproj" })
        Write(path, project);
    Write(Repository.CliProject, project.Replace("</Project>", """
        <ItemGroup><ProjectReference Include="../../Shared/Desktop/Desktop.csproj" /></ItemGroup></Project>
        """));
    Write("Shared/Desktop/Desktop.csproj", project);
    Write("Shared/Desktop/Supervisor.cs", "initial supervisor");
    Write("Collectors/Browser/Platforms/MacOS/Browser.cs", "Mac browser");
    Write("Collectors/Browser/Platforms/Windows/Browser.cs", "Windows browser");
    Write("Shared/Directory.Build.props", """
        <Project><ItemGroup Condition="'$(MSBuildProjectName)' == 'Capture'">
          <ProjectReference Include="$(MSBuildThisFileDirectory)Platform/Platform.csproj" />
        </ItemGroup></Project>
        """);
    Write("Collectors/Browser/Extension/manifest.json", """{"name":"Browser"}""");
    Write("Collectors/Browser/Extension/test/test.mjs", "test");
    Write("Collectors/Browser/Tests/test.cs", "test");
    Write("Shared/Platform/Tests/test.cs", "test");
    Write("Collectors/Browser/Program.cs", "browser");
    Write("Collectors/Codex/Program.cs", "codex");
    Write(".gitignore", "/Collectors/Browser/ignored\nbin/\nobj/\n");
    var icon = Path.Combine(source, MacHostBuilder.AppIconMaster);
    Directory.CreateDirectory(Path.GetDirectoryName(icon)!);
    File.Copy(Path.Combine(checkout, MacHostBuilder.AppIconMaster), icon);
    await Git("init", "-q");
    await Git("add", "-A");

    var index = Path.Combine(source, ".git", "index");
    var originalIndex = File.ReadAllBytes(index);
    var initial = await Id();
    Check(initial == await Id(), "same content produces the same identity");
    var hostBefore = await HostId();
    Write("Shared/Desktop/Supervisor.cs", "new supervisor");
    Check(await HostId() != hostBefore && await Id() == initial, "desktop supervision changes the host identity alone");
    var windows = await ComponentIdentity.Read(source, "Collectors/Browser", browser, "win-x64", RunProcess.Dotnet, default);
    Write("Collectors/Browser/Platforms/Windows/Browser.cs", "changed Windows browser");
    Check(await Id() == initial &&
        await ComponentIdentity.Read(source, "Collectors/Browser", browser, "win-x64", RunProcess.Dotnet, default) != windows,
        "only the target platform implementation contributes to a component identity");
    Check(ProgramDescription.ExecutableName("browser", "win-x64") == "orisumi-browser.exe" &&
        ProgramDescription.ExecutableName("browser", "osx-arm64") == "orisumi-browser" &&
        ReleaseVersion.AssetPrefix(new Version(5, 0, 0), "win-x64") == "orisumi-5.0.0-win-x64" &&
        !Platforms.Runs("macos", "win-x64"), "artifact names and eligible sources use the requested RID");

    Write("Collectors/Browser/ignored", "ignored");
    Check(initial == await Id(), "ignored untracked input does not change the identity");
    Write("Collectors/Browser/Program.cs", "dirty browser");
    Check(initial != await Id(), "unstaged input changes the identity");
    Write("Collectors/Browser/Program.cs", "browser");
    Write("Collectors/Browser/untracked.txt", "untracked");
    var untracked = await Id();
    Check(untracked != initial, "untracked input changes the identity");
    foreach (var path in new[] { "Collectors/Browser/Extension/test/test.mjs", "Collectors/Browser/Tests/test.cs",
        "Shared/Platform/Tests/test.cs", "Collectors/Codex/Program.cs" })
        File.AppendAllText(Path.Combine(source, path), " changed");
    Check(await Id() == untracked, "test directories and unrelated components are excluded");

    Write("Shared/Platform/Platform.csproj", project + "\n<!-- referenced -->");
    var referenced = await Id();
    Check(referenced != untracked, "transitive references evaluated through props count");
    Write("Shared/Directory.Build.props", File.ReadAllText(Path.Combine(source, "Shared/Directory.Build.props")) + "\n<!-- props -->");
    var beforeCommit = await Id();
    Check(beforeCommit != referenced, "ancestor Directory.Build.props counts");
    Check(File.ReadAllBytes(index).SequenceEqual(originalIndex), "the real index is byte-for-byte unchanged");

    await Git("add", "-A");
    var tree = (await Git("write-tree")).Trim();
    var identity = new Dictionary<string, string>
    {
        ["GIT_AUTHOR_NAME"] = "Orisumi fixture", ["GIT_AUTHOR_EMAIL"] = "fixture@example.invalid",
        ["GIT_COMMITTER_NAME"] = "Orisumi fixture", ["GIT_COMMITTER_EMAIL"] = "fixture@example.invalid",
    };
    var commit = (await Repository.Query("git", source, default, ["commit-tree", tree, "-m", "identity fixture"], identity)).Trim();
    await Git("update-ref", "HEAD", commit);
    Check((await Git("status", "--porcelain")).Length == 0 && beforeCommit == await Id(),
        "committing the same working tree content preserves the identity");
    Console.WriteLine("PASS: worktree identity, recursive MSBuild references, props, exclusions, clean tree and unchanged index");
    return source;

    Task<string> HostId() => ComponentIdentity.Read(source, "Host", Repository.CliProject, Platforms.CurrentRid, RunProcess.Dotnet, default);
    Task<string> Id() => ComponentIdentity.Read(source, "Collectors/Browser", browser, Platforms.CurrentRid, RunProcess.Dotnet, default);
    Task<string> Git(params string[] args) => Repository.Query("git", source, default, args);
    void Write(string name, string content)
    {
        var path = Path.Combine(source, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}

async Task RollbackChangedCollectors()
{
    var instance = Instance.Create(Under("transaction store"), "test_rollback");
    var fixture = Prepare("transaction system");
    var platform = new MacInstallation(instance, Locations(fixture), FixtureTools(fixture));
    var jobs = new MemoryJobs();
    var first = Package("transaction old", instance, null, ("claude", 'a', "old-claude"), ("codex", 'b', "old-codex"));
    await InstallCommand.Run(instance, first, jobs, platform, TextWriter.Null, default);
    foreach (var program in await CollectorPrograms.Read(instance.Store.Root, default, temp.FullName))
        await jobs.Set(program.Description.Name, program.Job(instance.Store), default);

    var previousManifest = File.ReadAllText(instance.Store.Manifest);
    var previousJobs = jobs.Jobs.ToDictionary();
    var previousPayloads = previousJobs.Keys.ToDictionary(name => name, name => ReleaseManifest.HashFiles(instance.Store.Collector(name)));
    var upgrade = Package("transaction update", instance, null, ("claude", 'c', "new-claude"), ("codex", 'd', "new-codex"));
    jobs.Changes.Clear();
    jobs.Fail = "codex";
    await ExpectAsync<IOException>(() => InstallCommand.Run(instance, upgrade, jobs, platform, TextWriter.Null, default), "codex");
    Check(jobs.Changes.SequenceEqual(["set claude", "set codex", "restore codex", "restore claude"]),
        "failure after changing the second registration restores both in reverse order");
    Check(File.ReadAllText(instance.Store.Manifest) == previousManifest &&
        previousJobs.All(item => jobs.Jobs[item.Key] == item.Value) &&
        previousPayloads.All(item => ReleaseManifest.HashFiles(instance.Store.Collector(item.Key)).SequenceEqual(item.Value)),
        "the previous manifest, payloads and job specifications agree after rollback");
    Console.WriteLine("PASS: second collector failure restores both registrations and both installed payloads");
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
    var extension = ChromeExtension.DirectoryFor(instance);
    ChromeExtension.CheckConfiguration(extension, instance);
    Check(File.ReadAllText(Path.Combine(extension, "manifest.json")).Contains("(dev)") &&
        !File.Exists(Path.Combine(first, "collectors/browser/extension/config.json")), "install configures only the staged extension");

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
    (manifest with { Platform = "other" }).Write(releasePath);
    await ExpectAsync<InvalidDataException>(() => Apply(badHost), "发布平台");
    (manifest with { Instance = "prod" }).Write(releasePath);
    await ExpectAsync<InvalidDataException>(() => Apply(badHost), "目标实例");
    manifest.Write(releasePath);
    File.AppendAllText(info, "tampered");
    await ExpectAsync<InvalidDataException>(() => Apply(badHost), "SHA-256");
    File.WriteAllText(info, original.Replace(instance.BundleIdentifier, "dev.orisumi"));
    RehashHost();
    await ExpectAsync<InvalidDataException>(() => Apply(badHost), "expected identifier");
    File.WriteAllText(info, original);
    File.WriteAllText(Path.Combine(hostDirectory, HostBundle.Name, "invalid-signature"), "");
    RehashHost();
    await ExpectAsync<InvalidDataException>(() => Apply(badHost), "strict signature verification failed");
    File.Delete(Path.Combine(hostDirectory, HostBundle.Name, "invalid-signature"));
    RehashHost();
    File.WriteAllText(releasePath, "{}");
    await ExpectAsync<JsonException>(() => Apply(badHost));
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

    using var status = new StringWriter();
    await CaptureStatus.Write(instance, new MacCollectorJobs(instance, locations), true, status, default);
    using var json = JsonDocument.Parse(status.ToString());
    var installedSource = json.RootElement.GetProperty("source");
    Check(installedSource.GetProperty("branch").GetString() == "work" && installedSource.GetProperty("commit").GetString() == "0123456" &&
        !installedSource.GetProperty("clean").GetBoolean() && json.RootElement.GetProperty("host_version").GetString()!.EndsWith(new string('1', 40)),
        "status reports dev source and installed component versions");
    status.GetStringBuilder().Clear();
    await CaptureStatus.Write(instance, new MacCollectorJobs(instance, locations), false, status, default);
    Check(status.ToString().Contains("work 0123456 dirty"), "text status reports the dev source");

    var prod = Instance.Create(Under("temporary prod"), "prod");
    var prodRelease = Package("prod release", prod, "v5.0.0", ("host", '5', "unused"));
    await InstallCommand.Run(prod, prodRelease, new MacCollectorJobs(prod, locations), new MacInstallation(prod, locations, FixtureTools(fixture)), output, default);
    Check(new FileInfo(Path.Combine(locations.Commands, "orisumi")).LinkTarget == prod.Store.HostExecutable, "only prod installs the command link");
    status.GetStringBuilder().Clear();
    await CaptureStatus.Write(prod, new MacCollectorJobs(prod, locations), true, status, default);
    using var prodStatus = JsonDocument.Parse(status.ToString());
    Check(prodStatus.RootElement.GetProperty("version").GetString() == "v5.0.0", "status reports the release version");

    Console.WriteLine("PASS: manifest install, selective restarts, rollback, signature and hash refusals, status and prod command link");

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

async Task Deployment(string source)
{
    var fixture = Prepare("deploy system");
    var locations = Locations(fixture);
    var root = Under("dev store '中文");
    var dotnet = Path.Combine(fixture, "dotnet-fixture");
    WriteExecutable(dotnet, $$"""
        #!/bin/sh
        set -eu
        if [ "$1" = msbuild ]; then exec {{Quote(RunProcess.Dotnet)}} "$@"; fi
        [ "$1 $3 $4 $5 $6 $7" = 'publish -c Release -r osx-arm64 -p:PublishAot=true' ]
        [ "$9 ${10} ${11} ${12}" = '-p:IncludeSourceRevisionInInformationalVersion=false -warnaserror -tl:off -o' ]
        version=${8#-p:InformationalVersion=}
        name=${2##*/}
        case "$name" in
          Orisumi.Cli.csproj) name=host; exe=orisumi ;;
          *) name=${name#Orisumi.Collectors.}
             name=$(printf '%s' "${name%.csproj}" | tr '[:upper:]' '[:lower:]')
             exe=orisumi-$name ;;
        esac
        out=${13}
        printf '%s\n' "$out" >> "$(dirname "$0")/publishes"
        if [ -f "$(dirname "$0")/fail-publish" ] && [ "$name" = codex ]; then exit 17; fi
        mkdir -p "$out"
        case "$name" in
          host) description='{"name":"host","version":"'"$version"'"}' ;;
          *) description='{"name":"'"$name"'","version":"'"$version"'","arguments":["capture"]}' ;;
        esac
        if [ "$name" = host ]; then
          cp /usr/bin/true "$out/$exe"
          mkdir -p "$out/../Resources"
          printf '%s\n' "$description" > "$out/../Resources/describe.json"
        else
          {
            printf '%s\n' '#!/bin/sh'
            printf "if [ \"\$1\" = schema ]; then cat '%s'; exit; fi\n" "$(dirname "$0")/schema-$name.json"
            printf '%s\n' '[ "$1" = describe ] || exit 2'
            printf "printf '%%s\\\\n' '%s'\n" "$description"
          } > "$out/$exe"
          chmod +x "$out/$exe"
        fi
        """);
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
        mkdir -p "$9/release"
        cp "$(dirname "$0")/badge" "$9/release/OrisumiApp"
        """);
    WriteExecutable(Path.Combine(fixture, "npm"), """
        #!/bin/sh
        set -eu
        [ -f manifest.json ]
        if [ "$*" = ci ]; then exit; fi
        [ "$#" = 5 ] && [ "$1 $2 $3 $4" = 'run build -- --outDir' ]
        mkdir -p "$5"
        printf 'extension\n' > "$5/worker.js"
        """);
    const string schema = """
        [{"name":"entries","columns":[
          {"name":"id","type":"INTEGER","notnull":0,"default":null,"pk":1},
          {"name":"required","type":"TEXT","notnull":1,"default":null,"pk":0},
          {"name":"optional","type":"TEXT","notnull":0,"default":null,"pk":0},
          {"name":"with_default","type":"TEXT","notnull":1,"default":"'default'","pk":0}
        ]},{"name":"new_table","columns":[{"name":"value","type":"TEXT","notnull":1,"default":null,"pk":0}]}]
        """;
    foreach (var name in new[] { "browser", "codex" }) File.WriteAllText(Path.Combine(fixture, $"schema-{name}.json"), schema);
    var path = Environment.GetEnvironmentVariable("PATH");
    using var output = new StringWriter();
    var host = FixtureTools(fixture, RunHost);
    try
    {
        Environment.SetEnvironmentVariable("PATH", fixture + Path.PathSeparator + path);
        var dirty = Path.Combine(source, "Collectors/Browser/untracked.txt");
        File.AppendAllText(dirty, " working edit");
        await Deploy.Run(source, root, ["browser"], Platforms.CurrentRid, dotnet, instance => new MacCollectorJobs(instance, locations), new MacHostBuilder(host), output, default);
        var instance = Instance.Open(root);
        var store = instance.Store;
        var manifest = ReleaseManifest.Read(store.Manifest);
        var hostFields = PlistFields(Path.Combine(store.Host, HostBundle.Name, "Contents", "Info.plist"));
        Check(instance.Name == "dev" && manifest.Version is null && !manifest.Source.Clean &&
            manifest.Source.Checkout == source && manifest.Components.Keys.Order().SequenceEqual(["browser", "host"]),
            "deploy installs the dirty working tree for dev and always includes the host");
        Check(manifest.InstalledAtUtcMs is > 0 && manifest.Schema["browser"][0].Columns[0].Name == "id",
            "install records its time and schema from the built collector");
        Check(hostFields["CFBundleIdentifier"].Value == instance.BundleIdentifier &&
            hostFields["CFBundleExecutable"].Value == HostBundle.AppExecutableName &&
            hostFields["CFBundleIconFile"].Value == "AppIcon.icns" && hostFields["LSUIElement"].Name == "true" &&
            hostFields["CFBundleVersion"].Value == manifest.Components["host"].Version &&
            hostFields["CFBundleName"].Value == "Orisumi Dev" && hostFields["CFBundleDisplayName"].Value == "Orisumi Dev" &&
            File.Exists(Path.Combine(store.Host, HostBundle.Name, "Contents", "Resources", "AppIcon.icns")) &&
            File.Exists(Path.Combine(store.Host, HostBundle.Name, "Contents", "Resources", "Mascot.png")) &&
            File.Exists(Path.Combine(ChromeExtension.DirectoryFor(instance), "worker.js")), "deploy builds the dev app name, badged icon, menu bar mascot and extension");
        var extensionManifest = Path.Combine(ChromeExtension.DirectoryFor(instance), "manifest.json");
        using var builtExtension = JsonDocument.Parse(File.ReadAllText(extensionManifest));
        Check(builtExtension.RootElement.GetProperty("version_name").GetString() == manifest.Components["browser"].Version,
            "the deployed extension reports the browser component version");
        using var sourceExtension = JsonDocument.Parse(File.ReadAllText(Path.Combine(source, DevCommand.ExtensionDirectory, "manifest.json")));
        Check(!sourceExtension.RootElement.TryGetProperty("version_name", out _), "the source extension manifest stays unlabelled");
        Check(File.ReadAllLines(Path.Combine(fixture, "host-events")).SequenceEqual(["capture start"]) &&
            output.ToString().Contains(ChromeExtension.DirectoryFor(instance)) &&
            output.ToString().Contains(ChromeExtension.Id(ChromeExtension.DirectoryFor(instance))), "first deploy starts capture and prints the new extension path and ID");

        output.GetStringBuilder().Clear();
        await Repository.Query("git", source, default, ["update-ref", "--no-deref", "HEAD", manifest.Source.Commit]);
        File.AppendAllText(dirty, " another edit");
        await Deploy.Run(source, root, [], Platforms.CurrentRid, dotnet, instance => new MacCollectorJobs(instance, locations), new MacHostBuilder(host), output, default);
        var updated = ReleaseManifest.Read(store.Manifest);
        Check(File.ReadAllLines(Path.Combine(fixture, "host-events")).Length == 1 &&
            !output.ToString().Contains("Chrome extension") && !output.ToString().Contains("host:") &&
            updated.Components.ContainsKey("codex") && updated.Source.Branch is null,
            "redeploy builds only changed components, keeps running capture, adds new collectors and prints no extension instructions");

        output.GetStringBuilder().Clear();
        var publishes = File.ReadAllLines(Path.Combine(fixture, "publishes")).Length;
        await Repository.Query("git", source, default, ["symbolic-ref", "HEAD", "refs/heads/review"]);
        await Repository.Query("git", source, default, ["update-ref", "refs/heads/review", updated.Source.Commit]);
        // Seed stale source fields with the same installed component identities.
        (updated with { Source = updated.Source with { Commit = new string('0', 40), Clean = true } }).Write(store.Manifest);
        await Deploy.Run(source, root, [], Platforms.CurrentRid, dotnet, instance => new MacCollectorJobs(instance, locations), new MacHostBuilder(host), output, default);
        var refreshed = ReleaseManifest.Read(store.Manifest);
        using var status = new StringWriter();
        await CaptureStatus.Write(instance, new MacCollectorJobs(instance, locations), true, status, default);
        using var statusJson = JsonDocument.Parse(status.ToString());
        var statusSource = statusJson.RootElement.GetProperty("source");
        Check(output.ToString().Contains("No component changed.") &&
            File.ReadAllLines(Path.Combine(fixture, "publishes")).Length == publishes &&
            File.ReadAllLines(Path.Combine(fixture, "host-events")).Length == 1 &&
            refreshed.HasSameComponents(updated) && refreshed.InstalledAtUtcMs == updated.InstalledAtUtcMs &&
            refreshed.Source.Commit == updated.Source.Commit && statusSource.GetProperty("branch").GetString() == "review" &&
            statusSource.GetProperty("commit").GetString() == updated.Source.Commit[..7] &&
            !statusSource.GetProperty("clean").GetBoolean(), "unchanged deploy refreshes status source without building or installing");

        var previous = File.ReadAllText(store.Manifest);
        File.WriteAllText(Path.Combine(fixture, "fail-publish"), "");
        File.WriteAllText(Path.Combine(source, "Collectors", "Codex", "edit.txt"), "changes codex so it is rebuilt");
        await ExpectAsync<IOException>(
            () => Deploy.Run(source, root, ["codex"], Platforms.CurrentRid, dotnet, instance => new MacCollectorJobs(instance, locations), new MacHostBuilder(host), output, default), "exited with code 17");
        Check(File.ReadAllText(store.Manifest) == previous && File.ReadAllLines(Path.Combine(fixture, "host-events")).Length == 1 &&
            File.ReadAllLines(Path.Combine(fixture, "publishes")).All(publish => !Directory.Exists(publish)),
            "failed publish preserves the installed manifest and removes temporary release paths");
        File.Delete(Path.Combine(fixture, "fail-publish"));
        File.Delete(Path.Combine(source, "Collectors", "Codex", "edit.txt"));
        await Release(source, fixture, dotnet, instance, host);
    }
    finally { Environment.SetEnvironmentVariable("PATH", path); }
    Console.WriteLine("PASS: deploy with a temporary dev store, dirty builds, component selection and capture start rules");

    async Task<int> RunHost(string executable, string[] arguments, TextWriter? writer, CancellationToken token)
    {
        Check(File.Exists(executable), "the host executable is present");
        if (arguments is ["describe"])
        {
            var resources = Path.Combine(Path.GetDirectoryName(executable)!, "..", "Resources");
            await writer!.WriteAsync(await File.ReadAllTextAsync(Path.Combine(resources, "describe.json"), token));
            return 0;
        }

        // Temporary bundles use a signature fixture. Invoke the host commands with the same paths and arguments.
        switch (arguments)
        {
            case ["--store", var store, "install", var release]:
                var fresh = HostBundle.Executable(ReleaseManifest.ComponentDirectory(release, "host"));
                Check(executable == (File.Exists(fresh) ? fresh : Instance.Open(store).Store.HostExecutable),
                    "install runs from the fresh release host, or the installed one when the host is unchanged");
                var target = Instance.Open(store);
                await InstallCommand.Run(target, release, new MacCollectorJobs(target, locations),
                    new MacInstallation(target, locations, FixtureTools(fixture, RunHost)), writer!, token);
                return 0;
            case ["--store", var store, "capture", "start"]:
                var instance = Instance.Open(store);
                Check(executable == instance.Store.HostExecutable, "capture is started from the installed host");
                File.AppendAllText(Path.Combine(fixture, "host-events"), "capture start\n");
                await CaptureStartup.Start(instance, new MacCollectorJobs(instance, locations), writer!, token);
                return 0;
            case ["--store", var store, "capture", "status"]:
                Check(executable == Instance.Open(store).Store.HostExecutable, "release reports the installed dev host's capture status");
                writer!.WriteLine("dev health attention");
                return 19;
            default:
                return 2;
        }
    }
}

async Task Release(string source, string fixture, string dotnet, Instance dev, HostBundle.Tools host)
{
    var remote = Directory.CreateDirectory(Path.Combine(fixture, "remote")).FullName;
    WriteExecutable(Path.Combine(fixture, "git"), """
        #!/bin/sh
        set -eu
        remote="$(dirname "$0")/remote"
        case "$*" in
          'rev-parse HEAD') cat "$remote/head" ;;
          'branch --show-current') cat "$remote/branch" ;;
          '--no-optional-locks status --porcelain=v1 --untracked-files=all') cat "$remote/status" ;;
          'fetch origin --tags --prune') cp "$remote/main" "$remote/fetched"; printf 'fetch\n' >> "$remote/events" ;;
          'for-each-ref --format=%(refname) %(objectname) refs/remotes/origin/main')
            printf 'refs/remotes/origin/main %s\n' "$(cat "$remote/fetched")" ;;
          'tag --list') cat "$remote/tags" ;;
          add\ *|write-tree) exec /usr/bin/git "$@" ;;
          *) exit 2 ;;
        esac
        """);
    WriteExecutable(Path.Combine(fixture, "gh"), """
        #!/bin/sh
        set -eu
        remote="$(dirname "$0")/remote"
        [ "$1" = release ]
        case "$2" in
          download)
            [ "$#" = 7 ] && [ "$4 $6" = '--pattern --dir' ]
            printf 'download %s\n' "$3" >> "$remote/events"
            cp "$remote/$3/$5" "$7/$5" ;;
          create)
            [ "$#" = 11 ] && [ "$4 $6 $8" = '--target --title --notes' ]
            [ "$5" = "$(cat "$remote/head")" ] && [ "$7" = "$3" ]
            printf 'create %s\n' "$3" >> "$remote/events"
            target="$remote/$3"
            mkdir "$target"
            cp "${10}" "${11}" "$target/"
            printf '%s\n' "$9" > "$target/notes"
            printf 'uploaded %s\n' "$3" ;;
          *) exit 2 ;;
        esac
        """);
    var installed = ReleaseManifest.Read(dev.Store.Manifest);
    using var output = new StringWriter();
    Write("head", installed.Source.Commit);
    Write("main", installed.Source.Commit);
    Write("branch", "review");
    Write("status", "");
    Write("tags", "v4.99.99\n");
    await Refused(null, 0, "main");
    Write("branch", "main");
    Write("status", " M README.md");
    await Refused(null, 0, "未提交");
    Check(!Events().Any(), "release refuses the branch and dirty tree before fetch");
    Write("status", "");
    Write("main", new string('a', 40));
    await Refused(null, 0, "请先推送");
    Check(Events().SequenceEqual(["fetch"]), "release checks the fetched origin/main");
    Write("main", installed.Source.Commit);
    Write("tags", "v5.9.7\nv4.99.99\n");
    await Refused("5.9.7", 0, "已存在");
    await ExpectAsync<ArgumentException>(() => Run("5.2"), "无效的发布版本");
    Check(!Events().Any(item => item.StartsWith("download") || item.StartsWith("create")), "early refusals use no gh operation");
    Write("tags", "v4.99.99\n");

    var components = new Dictionary<string, ReleaseComponent>(installed.Components)
    {
        ["codex"] = installed.Components["codex"] with { Identity = new string('0', 40) },
        ["extra"] = installed.Components["codex"],
    };
    components.Remove("browser");
    (installed with { Components = components }).Write(dev.Store.Manifest);
    await Refused(null, 0, "browser, codex, extra");
    installed.Write(dev.Store.Manifest);

    var publishLog = Path.Combine(fixture, "publishes");
    var builds = File.ReadAllLines(publishLog).Length;
    await Run(null);
    Check(File.ReadAllLines(publishLog).Length == builds + installed.Components.Count, "release builds every component even when dev matches");
    Check(output.ToString().Contains($"dev installed_at_utc_ms: {installed.InstalledAtUtcMs}") &&
        output.ToString().Contains("dev health attention") && output.ToString().Contains("exit 19"),
        "dev installation time and health are printed and do not gate release");
    Check(Events().Where(item => item.StartsWith("download") || item.StartsWith("create")).SequenceEqual(["create v5.0.0"]),
        "v4 tags do not affect the first v5 release or cause a previous manifest download");
    var first = ReadAssets("v5.0.0");
    Check(first.Version == "5.0.0" && first.Instance == "prod" && first.HasSameComponents(installed) &&
        first.Schema.Keys.Order().SequenceEqual(["browser", "codex"]), "release builds prod with the chosen version and collector schemas");
    Check(File.ReadAllLines(Path.Combine(remote, "v5.0.0", "notes")).All(line => line.Contains("changed (5.0.0+")),
        "first release notes list each component as changed");

    var priorTag = "v5.9.7";
    var priorVersion = priorTag[1..];
    var priorDir = Directory.CreateDirectory(Path.Combine(remote, priorTag)).FullName;
    var priorPath = Path.Combine(priorDir, $"orisumi-{priorVersion}-osx-arm64.json");
    Write("tags", "v5.9.3\nv5.8.99\nv5.9.7\nv4.99.99\nv6.0.0\n");
    var id = first.Schema["browser"][0].Columns[0];
    var removed = new SchemaColumn("removed", "TEXT", 0, null, 0);
    var prior = first with
    {
        Version = priorVersion,
        Schema = new(StringComparer.Ordinal)
        {
            ["browser"] = [new("entries", [id with { Type = "TEXT", NotNull = 1, Default = "0", Pk = 0 }, removed]), new("lost", [id])],
            ["codex"] = first.Schema["codex"],
            ["lost_source"] = [],
        },
    };
    prior.Write(priorPath);
    await Refused(null, 1, "lost_source: source removed", "browser.lost: table removed", "browser.entries.removed: column removed",
        "browser.entries.id: type changed", "browser.entries.id: notnull changed", "browser.entries.id: default changed",
        "browser.entries.id: pk changed", "browser.entries.required: new NOT NULL column has no default");
    Check(Events().Last() == "download v5.9.7", "schema checks download the numerically latest v5 manifest");

    prior.Schema.Remove("lost_source");
    prior.Schema["browser"] = [new("entries", [id, first.Schema["browser"][0].Columns[1]])];
    prior.Components["codex"] = first.Components["codex"] with { Identity = new string('0', 40) };
    prior.Write(priorPath);
    await Run(null);
    var next = ReadAssets("v5.10.0");
    Check(next.Version == "5.10.0", "automatic version increments the second number and resets the third");
    var notes = File.ReadAllText(Path.Combine(remote, "v5.10.0", "notes"));
    foreach (var name in new[] { "browser", "host" })
        Check(notes.Contains($"{name}: unchanged ({next.Components[name].Version})"), "unchanged release note has component version");
    Check(notes.Contains($"codex: changed ({next.Components["codex"].Version})"), "changed release note has component version");
    await Run("v5.12.3");
    Check(ReadAssets("v5.12.3").Version == "5.12.3", "explicit prefixed version is normalized");
    Console.WriteLine("PASS: release version selection, all refusals, additive schema rules, package hashes and notes via fake git and gh");

    Task Run(string? version) => ReleaseCommand.Run(source, version, dev.Store.Root, Platforms.CurrentRid, dotnet, output, default, new MacHostBuilder(host));
    void Write(string name, string value) => File.WriteAllText(Path.Combine(remote, name), value);
    string[] Events() => File.Exists(Path.Combine(remote, "events")) ? File.ReadAllLines(Path.Combine(remote, "events")) : [];

    async Task Refused(string? version, int uploads, params string[] messages)
    {
        try { await Run(version); }
        catch (InvalidDataException error)
        {
            Check(messages.All(message => error.Message.Contains(message)), error.Message);
            Check(Events().Count(line => line.StartsWith("create ")) == uploads, "a refused release attempted an upload");
            return;
        }
        throw new Exception("release should have refused: " + string.Join(", ", messages));
    }

    ReleaseManifest ReadAssets(string tag)
    {
        var directory = Path.Combine(remote, tag);
        var prefix = $"orisumi-{tag[1..]}-osx-arm64";
        Check(Directory.GetFiles(directory).Select(Path.GetFileName).Order().SequenceEqual(["notes", prefix + ".json", prefix + ".tar.gz"]),
            "release uploads exactly the versioned package and manifest");
        var release = ReleaseManifest.Read(Path.Combine(directory, prefix + ".json"));
        var archive = Path.Combine(directory, prefix + ".tar.gz");
        using var compressed = File.OpenRead(archive);
        Check(release.Package == new ReleasePackage(prefix + ".tar.gz", Convert.ToHexStringLower(SHA256.HashData(compressed))),
            "published manifest gives the package name and SHA-256");
        compressed.Position = 0;
        using var decompressed = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionMode.Decompress);
        using var tar = new System.Formats.Tar.TarReader(decompressed);
        ReleaseManifest? packaged = null;
        string? extensionVersion = null;
        while (tar.GetNextEntry() is { } entry)
        {
            switch (entry.Name.TrimStart('.', '/'))
            {
                case ReleaseManifest.FileName:
                    packaged = JsonSerializer.Deserialize(entry.DataStream!, ReleaseJson.Default.ReleaseManifest);
                    break;
                case "collectors/browser/extension/manifest.json":
                    using (var extension = JsonDocument.Parse(entry.DataStream!))
                        extensionVersion = extension.RootElement.GetProperty("version_name").GetString();
                    break;
            }
        }
        Check(packaged is not null && packaged.Package is null && packaged.HasSameComponents(release), "package includes the release directory manifest");
        Check(extensionVersion == release.Components["browser"].Version, "the packaged extension reports the browser component version");
        return release;
    }
}

async Task Update()
{
    var fixture = Prepare("update system");
    var locations = Locations(fixture);
    var host = FixtureTools(fixture);
    var certificateFile = Path.Combine(fixture, "certificate.der");
    File.WriteAllText(certificateFile, "fixture certificate distinct from the release certificate");
    var certificate = ReleaseManifest.HashFile(certificateFile);
    var prod = Instance.Create(Under("update prod"), "prod");
    var dev = Instance.Create(Under("update dev"), "dev");
    var store = prod.Store;
    var assets = Directory.CreateDirectory(Under("update assets")).FullName;
    const string databaseId = "00000000000000000000000000000001";
    const string legacyId = "00000000000000000000000000000002";
    var observation = Path.Combine(store.Observations("codex"), databaseId + ".sqlite");
    var legacy = Path.Combine(store.Observations("previous"), legacyId + ".sqlite");
    Directory.CreateDirectory(Path.GetDirectoryName(observation)!);
    Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
    using (var connection = Open(legacy))
        Sql(connection, "CREATE TABLE facts(value TEXT); INSERT INTO facts VALUES ('legacy')");
    using var writer = Open(observation);
    Sql(writer, $"CREATE TABLE database_identity(database_id TEXT); INSERT INTO database_identity VALUES ('{databaseId}'); CREATE TABLE facts(value TEXT)");
    Check(Sql(writer, "PRAGMA journal_mode=WAL") is "wal", "fixture must use WAL");
    Sql(writer, "PRAGMA wal_autocheckpoint=0");
    var baseline = ReleaseManifest.HashFile(observation);
    Sql(writer, "INSERT INTO facts VALUES ('first')");
    Check(ReleaseManifest.HashFile(observation) == baseline, "fixture row is in WAL");

    await Assets("5.0.1", 'a', 'b');
    await Assets("5.1.0", 'a', 'b');
    await Assets("5.2.0", 'a', 'c');
    await Assets("5.10.0", 'd', 'c');
    using var output = new StringWriter();
    await ExpectAsync<ArgumentException>(() => UpdateCommand.Run(dev, null, assets, new MacCollectorJobs(dev, locations), new MacInstallation(dev, locations, host, certificate), output, default), "prod");
    var archive = Asset("5.0.1", "tar.gz");
    File.Move(archive, archive + ".original");
    File.WriteAllText(archive, "tampered");
    await ExpectAsync<InvalidDataException>(() => Apply("5.0.1"), "安装包 SHA-256");
    File.Move(archive + ".original", archive, overwrite: true);
    await ExpectAsync<InvalidDataException>(() => UpdateCommand.Run(prod, "5.0.1", assets, new MacCollectorJobs(prod, locations), new MacInstallation(prod, locations, host), output, default), "证书 SHA-256");
    Check(!File.Exists(store.Manifest) && !Directory.Exists(store.Backups) && !Directory.Exists(locations.Agents) &&
        !Directory.Exists(locations.Commands), "dev, hash and certificate refusals leave the installation unchanged");

    await Apply("v5.0.1");
    Check(output.ToString().Contains("Updated pre-v5 -> 5.0.1") && ReleaseManifest.ReadInstalled(store)?.InstalledAtUtcMs is > 0 &&
        Snapshot("pre-v5", "codex", databaseId) == "first" && Snapshot("pre-v5", "previous", legacyId) == "legacy",
        "pre-v5 backup includes WAL contents and older databases without an identity");
    Check(new FileInfo(Path.Combine(locations.Commands, "orisumi")).LinkTarget == store.HostExecutable, "update installs through Phase A");
    await CaptureStartup.Start(prod, new MacCollectorJobs(prod, locations), output, default);
    var launchLog = Path.Combine(fixture, "launch-events");
    var firstEvents = File.ReadAllLines(launchLog).Length;

    File.Delete(Asset("5.1.0", "tar.gz"));
    var before = File.ReadAllText(store.Manifest);
    var hostAssembly = Path.Combine(checkout, "Host/Cli/bin/Debug/net11.0/orisumi.dll");
    using var cliOutput = new StringWriter();
    Check(await RunProcess.Run(RunProcess.Dotnet, [hostAssembly, "--store", store.Root, "update", "5.1.0", "--from", assets],
            cliOutput, default) == 0 && cliOutput.ToString().Contains("up to date") &&
        File.ReadAllText(store.Manifest) == before && File.ReadAllLines(launchLog).Length == firstEvents,
        "update --from exits before opening the archive when component identities already match");

    Sql(writer, "INSERT INTO facts VALUES ('second')");
    await Apply("5.2.0");
    Check(Snapshot("5.0.1", "codex", databaseId) == "first,second" &&
        ReleaseManifest.Read(store.Manifest).Components["host"].Version.StartsWith("5.0.1+") &&
        File.ReadAllLines(launchLog).Skip(firstEvents).SequenceEqual(["bootout dev.orisumi.prod.capture.codex", "bootstrap dev.orisumi.prod.capture.codex"]),
        "updating a collector snapshots current observations and restarts only the changed collector");
    Check(Directory.GetDirectories(store.Backups).Length == 2, "two completed backups are retained");

    Sql(writer, "INSERT INTO facts VALUES ('third')");
    await Apply(null);
    Check(output.ToString().Contains("Updated 5.2.0 -> 5.10.0") &&
        Snapshot("5.2.0", "codex", databaseId) == "first,second,third" &&
        Directory.GetDirectories(store.Backups).Select(Path.GetFileName).Order().SequenceEqual(["5.0.1", "5.2.0"]),
        "local latest uses numeric version order and prunes the oldest backup");

    await Apply("5.0.1");
    Check(output.ToString().Contains("Updated 5.10.0 -> 5.0.1") &&
        ReleaseManifest.Read(store.Manifest).Version == "5.0.1" &&
        Directory.GetDirectories(store.Backups).Select(Path.GetFileName).Order().SequenceEqual(["5.10.0", "5.2.0"]),
        "an explicit downgrade updates normally and retains the two most recent snapshots");
    Check(Snapshot("5.10.0", "previous", legacyId) == "legacy", "a copied database is still present in the newest backup");
    File.Delete(legacy);
    using var exported = new StringWriter();
    Check(StoreExport.Write(store.Root, Under("export after update"), exported) == 0, "export after update");
    using var json = JsonDocument.Parse(exported.ToString());
    Check(json.RootElement.GetProperty("databases").EnumerateArray().Single().GetProperty("database").GetString() == databaseId,
        "export only reads observations, excluding all backup databases");
    Console.WriteLine("PASS: local updates, up-to-date shortcut, hash and certificate refusals, backups, pruning, downgrade and export");

    Task Apply(string? version) => UpdateCommand.Run(prod, version, assets, new MacCollectorJobs(prod, locations), new MacInstallation(prod, locations, host, certificate), output, default);
    string Asset(string version, string extension) => Path.Combine(assets, $"orisumi-{version}-osx-arm64.{extension}");

    async Task Assets(string version, char hostId, char collectorId)
    {
        var directory = Package("update-release-" + version, prod, version, ("host", hostId, ""), ("codex", collectorId, "capture"));
        var release = ReleaseManifest.Read(Path.Combine(directory, ReleaseManifest.FileName));
        var path = Asset(version, "tar.gz");
        await Repository.Query("/usr/bin/tar", checkout, default, ["-czf", path, "-C", directory, "."]);
        (release with { Package = new(Path.GetFileName(path), ReleaseManifest.HashFile(path)) }).Write(Asset(version, "json"));
    }

    string? Snapshot(string version, string source, string id)
    {
        var path = Path.Combine(store.Backups, version, "observations", source, id + ".sqlite");
        Check(File.Exists(path), $"missing backup {path}");
        using var connection = Open(path);
        Check(Sql(connection, "PRAGMA journal_mode") is "delete", "a backup is a standalone database");
        return Sql(connection, "SELECT group_concat(value, ',') FROM (SELECT value FROM facts ORDER BY rowid)") as string;
    }

    static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    static object? Sql(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}

async Task ForwardingAndReset()
{
    var fixture = Prepare("reset system");
    var locations = Locations(fixture);
    var store = Instance.Create(Under("reset dev"), "dev").Store;
    var prod = Instance.Create(Under("reset prod"), "prod").Store;
    Directory.CreateDirectory(prod.Logs);
    await ExpectAsync<ArgumentException>(() => DevInstance.Reset(prod.Root, instance => new MacCollectorJobs(instance, locations), default), "prod");
    Check(Directory.Exists(prod.Logs), "reset leaves a prod store intact");

    var dev = (await Repository.Query(RunProcess.Dotnet, checkout, default,
        ["msbuild", Repository.DevProject, "-nologo", "-p:Configuration=Debug", "-getProperty:TargetPath"])).Trim();
    var missing = await InvokeDev("capture", "status", "--store", store.Root);
    Check(missing.Code != 0 && missing.Error.Contains("orisumi-dev deploy"), "missing dev host gives deployment instructions");
    Directory.CreateDirectory(Path.GetDirectoryName(store.HostExecutable)!);
    File.WriteAllText(store.HostExecutable, "");
    var launcher = Path.Combine(fixture, "forwarded-host");
    WriteExecutable(launcher, """
        #!/bin/sh
        printf '%s\n' "$@"
        exit 23
        """);
    string[] arguments = ["claude", "query", "--store", store.Root, "literal argument"];
    using var forwarded = new StringWriter();
    var forwardCode = await DevInstance.Forward(arguments, (executable, args, _, token) =>
    {
        Check(executable == store.HostExecutable, "forwarding discovers the installed host");
        return RunProcess.Run(launcher, args, forwarded, token, preserveExitCode: true);
    });
    Check(forwardCode == 23 && forwarded.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).SequenceEqual(arguments),
        "forwarding uses the installed dev host and preserves arguments and exit code");
    var refuse = await InvokeDev("capture", "stop", "--store", prod.Root);
    Check(refuse.Code != 0 && refuse.Error.Contains("prod"), "forwarding refuses a prod store override");

    foreach (var directory in new[] { store.ObservationsRoot, store.State, store.Logs })
        Directory.CreateDirectory(directory);
    var agent = Path.Combine(locations.Agents, "dev.orisumi.dev.capture.browser.plist");
    Directory.CreateDirectory(locations.Agents);
    File.WriteAllText(agent, "");
    File.WriteAllText(agent + ".loaded", "");
    await DevInstance.Reset(store.Root, instance => new MacCollectorJobs(instance, locations), default);
    Check(File.Exists(store.HostExecutable) && Instance.Open(store.Root).Name == "dev" && !File.Exists(agent) && !File.Exists(agent + ".loaded") &&
        new[] { store.ObservationsRoot, store.State, store.Logs }.All(directory => !Directory.Exists(directory)), "reset stops capture and clears only data");
    Console.WriteLine("PASS: forwarding to installed dev host, missing host guidance, prod refusal and dev reset");

    async Task<(int Code, string Output, string Error)> InvokeDev(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await RunProcess.Run(RunProcess.Dotnet, [dev, .. args], stdout, default, errors: stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }
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
        root=$(dirname "$0")
        case "$1" in
          print) [ -f "$root/agents/${2##*/}.plist.loaded" ] || exit 113; printf 'state = running\n' ;;
          bootout) rm "$root/agents/${2##*/}.plist.loaded"
                   printf 'bootout %s\n' "${2##*/}" >> "$root/launch-events" ;;
          bootstrap) label=${3##*/}
                     label=${label%.plist}
                     if [ -f "$root/fail-once" ] && [ "$(cat "$root/fail-once")" = "$label" ]; then rm "$root/fail-once"; exit 2; fi
                     touch "$3.loaded"
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
              --extract-certificates=*) cp "$(dirname "$0")/certificate.der" "${2#*=}0" ;;
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


sealed class MemoryJobs : ICollectorJobs
{
    internal sealed record Registration(string Name, JobSpec? Spec)
        : SavedJob(Name, Spec is not null, Spec is not null, Spec is null ? "not_loaded" : "waiting", null, null);

    internal Dictionary<string, Registration> Jobs { get; } = new();
    internal List<string> Changes { get; } = [];
    internal string? Fail { get; set; }

    public Task<IReadOnlyDictionary<string, SavedJob>> Inspect(IEnumerable<string> sources, CancellationToken token) =>
        Task.FromResult<IReadOnlyDictionary<string, SavedJob>>(sources.Union(Jobs.Keys)
            .ToDictionary(name => name, name => (SavedJob)(Jobs.GetValueOrDefault(name) ?? new Registration(name, null))));

    public Task<string> Set(string source, JobSpec? desired, CancellationToken token)
    {
        Changes.Add("set " + source);
        Jobs[source] = new Registration(source, desired);
        if (source == Fail) throw new IOException(source);
        return Task.FromResult(source);
    }

    public Task Restore(SavedJob previous, CancellationToken token)
    {
        Changes.Add("restore " + previous.Source);
        Jobs[previous.Source] = (Registration)previous;
        return Task.CompletedTask;
    }
}
