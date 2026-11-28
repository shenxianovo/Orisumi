#!/usr/bin/env dotnet
#:project ../Host/Cli/Orisumi.Cli.csproj
#:project ../Tools/Dev/Orisumi.Dev.csproj
#:include Fixtures/FixtureProgram.cs
#:include Fixtures/TemporaryGit.cs
#:include Fixtures/MemoryJobs.cs
#:property AssemblyName=InstanceChecks
#:property PublishAot=false

using System.CommandLine;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Orisumi.Cli;
using Orisumi.Cli.Capture;
using Orisumi.Dev;
using Orisumi.Desktop;
using Orisumi.Core;

var checkout = Environment.CurrentDirectory;
var temp = Directory.CreateTempSubdirectory("orisumi-instance-check-");
FixtureProgram program;
try
{
    program = await FixtureProgram.Build(Under("fixture tool"));
    Resolution();
    TaskDefinition();
    await AppLogin();
    var source = await Identity();
    await Install();
    await RollbackChangedCollectors();
    await Deployment(source);
    await Update();
    await ForwardingAndReset();
    Console.WriteLine("Instance, identity, install, deploy, release and update checks passed.");
    return 0;
}
finally { TemporaryGit.Delete(temp); }

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
    var other = Instance.Create(Under("other"), "test_codex");
    Check(prod.BrowserPort == 47810 && dev.BrowserPort == 47811 && other.BrowserPort == 47913, "instance ports");
    Check(Path.GetFileName(Instance.ProductionRoot()) == "Orisumi" &&
        Path.GetFileName(Instance.DevelopmentRoot()) == "Orisumi-dev", "default instance roots");
    Expect<ArgumentException>(() => Instance.Create(Under("invalid"), "../prod"));
}

void TaskDefinition()
{
    const string root = @"C:\Users\A & B\Orisumi-dev";
    const string user = "S-1-5-21-100";
    var supervisor = root + @"\app\host\orisumi-supervisor.exe";
    var app = root + @"\app\host\orisumi-app.exe";
    Verify(WindowsTaskDefinition.Capture(user, supervisor, root), supervisor, capture: true);
    Verify(WindowsTaskDefinition.App(user, app), app, capture: false);

    void Verify(XDocument xml, string command, bool capture)
    {
        var task = XDocument.Parse(xml.ToString()).Root!;
        var ns = task.Name.Namespace;
        string? Value(params string[] names) => names.Aggregate((XElement?)task, (node, name) => node?.Element(ns + name))?.Value;
        Check(ns.NamespaceName == "http://schemas.microsoft.com/windows/2004/02/mit/task" &&
            Value("Triggers", "LogonTrigger", "UserId") == user && Value("Principals", "Principal", "UserId") == user &&
            Value("Principals", "Principal", "LogonType") == "InteractiveToken" &&
            Value("Principals", "Principal", "RunLevel") == "LeastPrivilege", "interactive, least-privilege logon for the specified user");
        Check(Value("Settings", "MultipleInstancesPolicy") == "IgnoreNew" && Value("Settings", "ExecutionTimeLimit") == "PT0S" &&
            Value("Settings", "DisallowStartIfOnBatteries") == "false" && Value("Settings", "StopIfGoingOnBatteries") == "false",
            "unlimited runtime on AC and battery");
        Check(capture ? Value("Settings", "RestartOnFailure", "Interval") == "PT1M" && Value("Settings", "RestartOnFailure", "Count") == "999"
            : Value("Settings", "RestartOnFailure") is null, "only capture restarts on failure");
        Check(Value("Actions", "Exec", "Command") == command && Value("Actions", "Exec", "Arguments") == (capture ? $"--store \"{root}\"" : null),
            "task action preserves paths and passes --store only to capture");
    }
}

async Task AppLogin()
{
    foreach (var previous in new ScheduledTaskState?[] { null, new("old", false), new("old", true) })
    {
        var task = new FixtureAppTask(previous);
        var login = new WindowsLoginRegistration(task, default);
        await login.Apply(default);
        Check(task.State == new ScheduledTaskState("new", true) && task.Starts == (previous?.Active == true ? 0 : 1),
            "login registers the new task and starts only an idle app");
        await login.Restore(default);
        Check(task.State == previous && task.Stops == (previous?.Active == true ? 0 : 1),
            "rollback restores the previous definition and running state");
    }
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
        "Shared/Capture/Capture.csproj", "Shared/Core/Core.csproj" })
        Write(path, project);
    Write(Repository.CliProject, project.Replace("</Project>", """
        <ItemGroup><ProjectReference Include="../../Shared/Desktop/Desktop.csproj" /></ItemGroup></Project>
        """));
    Write("Shared/Desktop/Desktop.csproj", project);
    Write(Repository.SupervisorProject, project.Replace("</Project>", """
        <ItemGroup><ProjectReference Include="../../../../Shared/SupervisorDependency/Dependency.csproj" /></ItemGroup></Project>
        """));
    Write("Shared/SupervisorDependency/Dependency.csproj", project);
    Write(Repository.AppProject, project.Replace("</Project>", """
        <ItemGroup><ProjectReference Include="../../../../Shared/AppDependency/Dependency.csproj" /></ItemGroup></Project>
        """));
    Write("Shared/AppDependency/Dependency.csproj", project);
    Write("Shared/Desktop/Installation.cs", "initial installation");
    Write("Collectors/Browser/Platforms/MacOS/Browser.cs", "Mac browser");
    Write("Collectors/Browser/Platforms/Windows/Browser.cs", "Windows browser");
    Write("Shared/Directory.Build.props", """
        <Project><ItemGroup Condition="'$(MSBuildProjectName)' == 'Capture'">
          <ProjectReference Include="$(MSBuildThisFileDirectory)Core/Core.csproj" />
        </ItemGroup></Project>
        """);
    Write("Collectors/Browser/Extension/manifest.json", """{"name":"Browser"}""");
    Write("Collectors/Browser/Extension/test/test.mjs", "test");
    Write("Collectors/Browser/Tests/test.cs", "test");
    Write("Shared/Core/Tests/test.cs", "test");
    Write("Collectors/Browser/Program.cs", "browser");
    Write("Collectors/Codex/Program.cs", "codex");
    Write(".gitignore", "/Collectors/Browser/ignored\nbin/\nobj/\n");
    await Git("init", "-q");
    await Git("add", "-A");

    var index = Path.Combine(source, ".git", "index");
    var originalIndex = File.ReadAllBytes(index);
    var initial = await Id();
    Check(initial == await Id(), "same content produces the same identity");
    var hostBefore = await HostId(Platforms.CurrentRid);
    Write("Shared/Desktop/Installation.cs", "updated installation");
    Check(await HostId(Platforms.CurrentRid) != hostBefore && await Id() == initial, "desktop installation changes the host identity alone");
    var macHost = await HostId("osx-arm64");
    var windowsHost = await HostId("win-x64");
    Write("Host/Platforms/MacOS/App/App.swift", "updated mac app");
    Check(await HostId("osx-arm64") != macHost && await HostId("win-x64") == windowsHost,
        "the Swift app contributes only to the macOS host identity");
    macHost = await HostId("osx-arm64");
    Write("Host/Platforms/Windows/Supervisor/Program.cs", "updated Windows supervisor");
    Check(await HostId("win-x64") != windowsHost && await HostId("osx-arm64") == macHost,
        "the Windows supervisor contributes only to the Windows host identity");
    windowsHost = await HostId("win-x64");
    Write("Shared/SupervisorDependency/Dependency.cs", "updated dependency");
    Check(await HostId("win-x64") != windowsHost && await HostId("osx-arm64") == macHost,
        "a reference unique to the supervisor contributes only to the Windows host identity");
    windowsHost = await HostId("win-x64");
    Write("Host/Platforms/Windows/App/Icon/AppIcon.png", "updated tray icon");
    Check(await HostId("win-x64") != windowsHost && await HostId("osx-arm64") == macHost,
        "the tray app contributes only to the Windows host identity");
    windowsHost = await HostId("win-x64");
    Write("Shared/AppDependency/Dependency.cs", "updated app dependency");
    Check(await HostId("win-x64") != windowsHost && await HostId("osx-arm64") == macHost,
        "a reference unique to the tray app contributes only to the Windows host identity");
    var otherRid = Platforms.Current == "macos" ? "win-x64" : "osx-arm64";
    var otherPlatform = Platforms.Current == "macos" ? "Windows" : "MacOS";
    var otherIdentity = await Id(otherRid);
    Write($"Collectors/Browser/Platforms/{otherPlatform}/Browser.cs", "changed other platform");
    Check(await Id() == initial && await Id(otherRid) != otherIdentity,
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
        "Shared/Core/Tests/test.cs", "Collectors/Codex/Program.cs" })
        File.AppendAllText(Path.Combine(source, path), " changed");
    Check(await Id() == untracked, "test directories and unrelated components are excluded");

    Write("Shared/Core/Core.csproj", project + "\n<!-- referenced -->");
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

    Task<string> HostId(string rid) => ComponentIdentity.Read(source, "Host", Repository.HostProjects(rid), rid, RunProcess.Dotnet, default);
    Task<string> Id(string? rid = null) => ComponentIdentity.Read(source, "Collectors/Browser", [browser], rid ?? Platforms.CurrentRid, RunProcess.Dotnet, default);
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
    var platform = new FixtureInstallation();
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
    var instance = Instance.Create(Under("selective install"), "dev");
    var jobs = new MemoryJobs();
    var platform = new FixtureInstallation();
    Task Apply(string package) => InstallCommand.Run(instance, package, jobs, platform, TextWriter.Null, default);
    var first = Package("initial", instance, null, ("host", 'a', ""), ("browser", 'b', "capture"), ("codex", 'c', "capture"));
    await Apply(first);
    Check(jobs.Changes.Count == 0, "first install leaves capture stopped");
    var extension = ChromeExtension.DirectoryFor(instance);
    ChromeExtension.CheckConfiguration(extension, instance);
    Check(File.ReadAllText(Path.Combine(extension, "manifest.json")).Contains("(dev)") &&
        !File.Exists(Path.Combine(first, "collectors/browser/extension/config.json")), "install configures only the staged extension");
    await CaptureStartup.Start(instance, jobs, TextWriter.Null, default);
    await jobs.Set("codex", null, default);
    jobs.Changes.Clear();
    await Apply(Package("changed", instance, null, ("browser", 'd', "new-args"), ("codex", 'e', "stopped"), ("claude", 'f', "capture")));
    Check(jobs.Changes.SequenceEqual(["set browser", "set claude"]) &&
        jobs.Jobs["browser"].Spec!.Command[^1] == "new-args" && jobs.Jobs["codex"].Spec is null,
        "active replacements use their new arguments, new jobs start and stopped replacements stay stopped");
    jobs.Changes.Clear();
    await Apply(Package("same host", instance, null, ("host", 'a', "")));
    Check(jobs.Changes.Count == 0 && ReleaseManifest.Read(instance.Store.Manifest).Components.Count == 4,
        "identical and omitted components keep their registrations and manifest entries");
    var previous = File.ReadAllText(instance.Store.Manifest);
    var invalid = Package("invalid release", instance, null, ("host", '1', ""));
    var path = Path.Combine(invalid, ReleaseManifest.FileName);
    var manifest = ReleaseManifest.Read(path);
    (manifest with { Platform = "other" }).Write(path);
    await ExpectAsync<InvalidDataException>(() => Apply(invalid), "发布平台");
    (manifest with { Instance = "prod" }).Write(path);
    await ExpectAsync<InvalidDataException>(() => Apply(invalid), "目标实例");
    manifest.Write(path);
    File.AppendAllText(Store.HostExecutableIn(ReleaseManifest.ComponentDirectory(invalid, "host")), "tampered");
    await ExpectAsync<InvalidDataException>(() => Apply(invalid), "SHA-256");
    File.WriteAllText(path, "{}");
    await ExpectAsync<JsonException>(() => Apply(invalid));
    Check(File.ReadAllText(instance.Store.Manifest) == previous && jobs.Changes.Count == 0,
        "invalid releases preserve the manifest and registrations");
    using var status = new StringWriter();
    await CaptureStatus.Write(instance, jobs, false, status, default);
    Check(status.ToString().Contains("work 0123456 dirty"), "text status reports the installed dev source");
    await CaptureStartup.Stop(instance, jobs, TextWriter.Null, default);
    Check(jobs.Jobs.Values.All(job => !job.Supervised), "stop removes every active job");
}

async Task Deployment(string source)
{
    var fixture = Directory.CreateDirectory(Under("deploy tools")).FullName;
    var jobs = new MemoryJobs();
    var platform = new FixtureInstallation();
    var root = Under("dev store '中文");
    var dotnet = program.Tool(fixture, "publish-fixture");
    File.WriteAllText(Path.Combine(fixture, "dotnet-path"), RunProcess.Dotnet);
    program.Tool(fixture, "npm");
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
    var host = new FixtureHost(RunHost);
    try
    {
        Environment.SetEnvironmentVariable("PATH", fixture + Path.PathSeparator + path);
        var dirty = Path.Combine(source, "Collectors/Browser/untracked.txt");
        File.AppendAllText(dirty, " working edit");
        await Deploy.Run(source, root, ["browser"], Platforms.CurrentRid, dotnet, _ => jobs, host, output, default);
        var instance = Instance.Open(root);
        var store = instance.Store;
        var manifest = ReleaseManifest.Read(store.Manifest);
        Check(instance.Name == "dev" && manifest.Version is null && !manifest.Source.Clean &&
            manifest.Source.Checkout == source && manifest.Components.Keys.Order().SequenceEqual(["browser", "host"]),
            "deploy installs the dirty working tree for dev and always includes the host");
        Check(manifest.InstalledAtUtcMs is > 0 && manifest.Schema["browser"][0].Columns[0].Name == "id",
            "install records its time and schema from the built collector");
        Check(File.Exists(Path.Combine(ChromeExtension.DirectoryFor(instance), "worker.js")), "the extension was built");
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
        await Deploy.Run(source, root, [], Platforms.CurrentRid, dotnet, _ => jobs, host, output, default);
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
        await Deploy.Run(source, root, [], Platforms.CurrentRid, dotnet, _ => jobs, host, output, default);
        var refreshed = ReleaseManifest.Read(store.Manifest);
        using var status = new StringWriter();
        await CaptureStatus.Write(instance, jobs, true, status, default);
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
            () => Deploy.Run(source, root, ["codex"], Platforms.CurrentRid, dotnet, _ => jobs, host, output, default), "exited with code 17");
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
        if (arguments is ["describe"]) return await RunProcess.Run(executable, arguments, writer, token);

        switch (arguments)
        {
            case ["--store", var store, "install", var release]:
                var fresh = Store.HostExecutableIn(ReleaseManifest.ComponentDirectory(release, "host"));
                Check(executable == (File.Exists(fresh) ? fresh : Instance.Open(store).Store.HostExecutable),
                    "install runs from the fresh release host, or the installed one when the host is unchanged");
                var target = Instance.Open(store);
                await InstallCommand.Run(target, release, jobs, platform, writer!, token);
                return 0;
            case ["--store", var store, "capture", "start"]:
                var instance = Instance.Open(store);
                Check(executable == instance.Store.HostExecutable, "capture is started from the installed host");
                File.AppendAllText(Path.Combine(fixture, "host-events"), "capture start\n");
                await CaptureStartup.Start(instance, jobs, writer!, token);
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

async Task Release(string source, string fixture, string dotnet, Instance dev, IHostBuilder host)
{
    var remote = Directory.CreateDirectory(Path.Combine(fixture, "remote")).FullName;
    program.Tool(fixture, "gh");
    var installed = ReleaseManifest.Read(dev.Store.Manifest);
    using var output = new StringWriter();
    var origin = Under("origin.git");
    await Git("init", "--bare", "--initial-branch=main", origin);
    await Git("remote", "add", "origin", origin);
    await Refused(null, 0, "main");
    await Git("switch", "-C", "main");
    await Refused(null, 0, "未提交");
    Check(!File.Exists(Path.Combine(source, ".git", "FETCH_HEAD")), "release refuses a dirty tree before fetch");
    await Git("add", "-A");
    await Git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false",
        "commit", "-m", "tested component inputs");
    await Refused(null, 0, "请先推送");
    Check(File.Exists(Path.Combine(source, ".git", "FETCH_HEAD")), "release fetched the local origin");
    await Git("push", "origin", "main");
    File.WriteAllText(Path.Combine(remote, "head"), (await Git("rev-parse", "HEAD")).Trim());
    await Git("tag", "v4.99.99");
    await Git("tag", "v5.9.7");
    await Refused("5.9.7", 0, "已存在");
    await ExpectAsync<ArgumentException>(() => Run("5.2"), "无效的发布版本");
    Check(Events().Length == 0, "early refusals use no gh operation");
    await Git("tag", "-d", "v5.9.7");

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
    var priorPath = Path.Combine(priorDir, $"orisumi-{priorVersion}-{Platforms.CurrentRid}.json");
    foreach (var tag in new[] { "v5.9.3", "v5.8.99", priorTag, "v6.0.0" }) await Git("tag", tag);
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
    Console.WriteLine("PASS: release version selection, all refusals, additive schema rules, package hashes and notes via local origin and fake gh");

    Task Run(string? version) => ReleaseCommand.Run(source, version, dev.Store.Root, Platforms.CurrentRid, dotnet, output, default, host);
    Task<string> Git(params string[] arguments) => Repository.Query("git", source, default, arguments);
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
        var prefix = $"orisumi-{tag[1..]}-{Platforms.CurrentRid}";
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
    var jobs = new MemoryJobs();
    var platform = new FixtureInstallation();
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
    var baseline = DatabaseHash(observation);
    Sql(writer, "INSERT INTO facts VALUES ('first')");
    Check(DatabaseHash(observation) == baseline, "fixture row is in WAL");

    await Assets("5.0.1", 'a', 'b');
    await Assets("5.1.0", 'a', 'b');
    await Assets("5.2.0", 'a', 'c');
    await Assets("5.10.0", 'd', 'c');
    using var output = new StringWriter();
    await ExpectAsync<ArgumentException>(() => UpdateCommand.Run(dev, null, assets, jobs, platform, output, default), "prod");
    var archive = Asset("5.0.1", "tar.gz");
    File.Move(archive, archive + ".original");
    File.WriteAllText(archive, "tampered");
    await ExpectAsync<InvalidDataException>(() => Apply("5.0.1"), "安装包 SHA-256");
    File.Move(archive + ".original", archive, overwrite: true);
    Check(!File.Exists(store.Manifest) && !Directory.Exists(store.Backups), "hash refusal leaves the store unchanged");

    await Apply("v5.0.1");
    using var status = new StringWriter();
    await CaptureStatus.Write(prod, jobs, true, status, default);
    using var prodStatus = JsonDocument.Parse(status.ToString());
    Check(prodStatus.RootElement.GetProperty("version").GetString() == "5.0.1", "status reports the installed release version");
    Check(output.ToString().Contains("Updated pre-v5 -> 5.0.1") && ReleaseManifest.ReadInstalled(store)?.InstalledAtUtcMs is > 0 &&
        Snapshot("pre-v5", "codex", databaseId) == "first" && Snapshot("pre-v5", "previous", legacyId) == "legacy",
        "pre-v5 backup includes WAL contents and older databases without an identity");
    await CaptureStartup.Start(prod, jobs, output, default);
    jobs.Changes.Clear();

    File.Delete(Asset("5.1.0", "tar.gz"));
    var before = File.ReadAllText(store.Manifest);
    using var cliOutput = new StringWriter();
    var command = Commands.Create(new DesktopServices(_ => jobs, _ => platform), cliOutput);
    Check(await command.Parse(["--store", store.Root, "update", "5.1.0", "--from", assets]).InvokeAsync() == 0 &&
        cliOutput.ToString().Contains("up to date") && File.ReadAllText(store.Manifest) == before && jobs.Changes.Count == 0,
        "update --from exits before opening the archive when component identities already match");

    Sql(writer, "INSERT INTO facts VALUES ('second')");
    await Apply("5.2.0");
    Check(Snapshot("5.0.1", "codex", databaseId) == "first,second" &&
        ReleaseManifest.Read(store.Manifest).Components["host"].Version.StartsWith("5.0.1+") &&
        jobs.Changes.SequenceEqual(["set codex"]),
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
    Console.WriteLine("PASS: local updates, up-to-date shortcut, hash refusals, backups, pruning, downgrade and export");

    Task Apply(string? version) => UpdateCommand.Run(prod, version, assets, jobs, platform, output, default);
    string Asset(string version, string extension) => Path.Combine(assets, $"orisumi-{version}-{Platforms.CurrentRid}.{extension}");

    async Task Assets(string version, char hostId, char collectorId)
    {
        var directory = Package("update-release-" + version, prod, version, ("host", hostId, ""), ("codex", collectorId, "capture"));
        var release = ReleaseManifest.Read(Path.Combine(directory, ReleaseManifest.FileName));
        var path = Asset(version, "tar.gz");
        await Repository.Query("tar", checkout, default, ["-czf", path, "-C", directory, "."]);
        (release with { Package = new(Path.GetFileName(path), ReleaseManifest.HashFile(path)) }).Write(Asset(version, "json"));
    }

    static string DatabaseHash(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(file));
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
    var jobs = new MemoryJobs();
    var store = Instance.Create(Under("reset dev"), "dev").Store;
    var prod = Instance.Create(Under("reset prod"), "prod").Store;
    Directory.CreateDirectory(prod.Logs);
    await ExpectAsync<ArgumentException>(() => DevInstance.Reset(prod.Root, _ => jobs, default), "prod");
    Check(Directory.Exists(prod.Logs), "reset leaves a prod store intact");

    var dev = (await Repository.Query(RunProcess.Dotnet, checkout, default,
        ["msbuild", Repository.DevProject, "-nologo", "-p:Configuration=Debug", "-getProperty:TargetPath"])).Trim();
    var missing = await InvokeDev("capture", "status", "--store", store.Root);
    Check(missing.Code != 0 && missing.Error.Contains("orisumi-dev deploy"), "missing dev host gives deployment instructions");
    Directory.CreateDirectory(Path.GetDirectoryName(store.HostExecutable)!);
    File.WriteAllText(store.HostExecutable, "");
    string[] arguments = ["claude", "query", "--store", store.Root, "literal argument"];
    var forwardCode = await DevInstance.Forward(arguments, (executable, args, _, _) =>
    {
        Check(executable == store.HostExecutable && args.SequenceEqual(arguments), "forward discovers the installed host and preserves arguments");
        return Task.FromResult(23);
    });
    Check(forwardCode == 23, "forward preserves the host exit code");
    var refuse = await InvokeDev("capture", "stop", "--store", prod.Root);
    Check(refuse.Code != 0 && refuse.Error.Contains("prod"), "forwarding refuses a prod store override");

    foreach (var directory in new[] { store.ObservationsRoot, store.State, store.Logs })
        Directory.CreateDirectory(directory);
    await jobs.Set("browser", new JobSpec(["capture"], store.Root, "out", "err"), default);
    await DevInstance.Reset(store.Root, _ => jobs, default);
    Check(File.Exists(store.HostExecutable) && Instance.Open(store.Root).Name == "dev" && jobs.Jobs["browser"].Spec is null &&
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
        var executable = name == "host" ? Store.HostExecutableIn(component)
            : Path.Combine(component, ProgramDescription.ExecutableName(name, Platforms.CurrentRid));
        program.Describe(executable, name, version, name == "host" ? null : [argument]);
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

string Under(string path) => Path.Combine(temp.FullName, path);

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

sealed class FixtureHost(CommandProcess run) : IHostBuilder
{
    public CommandProcess Run => run;
    public Task Complete(string checkout, string directory, string instance, string projectVersion, string version,
        CancellationToken token) => Task.CompletedTask;
}

sealed class FixtureAppTask(ScheduledTaskState? previous) : IWindowsScheduledTask
{
    public string Name => "App";
    public ScheduledTaskState? State { get; private set; } = previous;
    public int Starts { get; private set; }
    public int Stops { get; private set; }
    public ScheduledTaskState? Query(CancellationToken token) => State;
    public void Register(string? xml, CancellationToken token) => State = new(xml ?? "new", State?.Active == true);
    public void Start(CancellationToken token) { Starts++; State = State! with { Active = true }; }
    public void End(CancellationToken token) { Stops++; State = State! with { Active = false }; }
    public void Delete(CancellationToken token) => State = null;
}

sealed class FixtureInstallation : IDesktopInstallation, ILoginRegistration
{
    public string HostExecutable(string directory) => Store.HostExecutableIn(directory);
    public CommandProcess RunHost => RunProcess.RunCommand;
    public Task VerifyHost(string directory, bool requireReleaseCertificate, CancellationToken token) => Task.CompletedTask;
    public async Task ExtractPackage(string archive, string directory, CancellationToken token)
    {
        using var file = File.OpenRead(archive);
        using var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress);
        await System.Formats.Tar.TarFile.ExtractToDirectoryAsync(gzip, directory, overwriteFiles: false, token);
    }
    public Task<ILoginRegistration> PrepareLogin(CancellationToken token) => Task.FromResult<ILoginRegistration>(this);
    public Task Apply(CancellationToken token) => Task.CompletedTask;
    public Task Restore(CancellationToken token) => Task.CompletedTask;
    public void LinkCommand() { }

    public Action ActivateDirectory(string staged, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var replace = Directory.Exists(destination);
        var previous = staged + ".old";
        if (replace) Directory.Move(destination, previous);
        Directory.Move(staged, destination);
        if (replace) Directory.Move(previous, staged);
        return () =>
        {
            Directory.Move(destination, previous);
            if (replace) Directory.Move(staged, destination);
            Directory.Move(previous, staged);
        };
    }
}
