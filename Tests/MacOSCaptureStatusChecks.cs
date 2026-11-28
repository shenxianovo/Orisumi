#:project ../Host/Cli/Orisumi.Cli.csproj
#:property AssemblyName=CaptureStatusChecks
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=false

using System.Runtime.Versioning;
using Orisumi.Desktop;
using Orisumi.Core;

[assembly: SupportedOSPlatform("macos")]

var temp = Directory.CreateTempSubdirectory("orisumi-capture-status-");
try
{
    var instance = Instance.Create(Path.Combine(temp.FullName, "store '中文"), "test_status");
    var locations = new SystemLocations(Path.Combine(temp.FullName, "agents"), Path.Combine(temp.FullName, "commands"),
        Path.Combine(temp.FullName, "launchctl"));
    WriteExecutable(locations.Launchctl, """
        #!/bin/sh
        set -eu
        [ "$1" = print ] && [ "$#" = 2 ] || exit 2
        case "$2" in
          gui/"$(/usr/bin/id -u)"/dev.orisumi.test_status.capture.*) ;;
          *) exit 2 ;;
        esac
        case "$2" in
          *.collecting) printf '\tstate = running\n\tpid = 4321\n\tlast exit code = 4\n' ;;
          *.idle) printf '\tstate = waiting\n\tlast exit code = 17\n' ;;
          *.queued) printf '\tstate = spawn scheduled\n\tlast exit code = (never exited)\n' ;;
          *.signaled) printf '\tstate = waiting\n\tlast exit code = terminated by signal\n' ;;
          *.broken) printf 'print failed\n' >&2; exit 5 ;;
          *) exit 113 ;;
        esac
        """);
    Directory.CreateDirectory(locations.Agents);
    var other = Instance.Create(Path.Combine(temp.FullName, "other"), "other");
    foreach (var label in new[] { instance.CaptureAgentLabel("idle"), instance.CaptureAgentLabel("queued"),
        instance.CaptureAgentLabel("signaled"), other.CaptureAgentLabel("ignored"), instance.AppAgentLabel })
        File.WriteAllText(Path.Combine(locations.Agents, label + ".plist"), "");
    var before = Entries(locations.Agents);
    var jobs = new MacCollectorJobs(instance, locations);
    var found = await jobs.Inspect(["collecting", "unloaded"], default);
    Check(found.Keys.Order().SequenceEqual(["collecting", "idle", "queued", "signaled", "unloaded"]), "only instance capture plists count");
    Check(found["collecting"] is { Registered: false, Supervised: true, State: "running", Pid: 4321, LastExitCode: null } &&
        found["idle"] is { Registered: true, State: "waiting" } && found["idle"].LastExitCode!.GetValue<long>() == 17 &&
        found["queued"] is { State: "spawn scheduled", LastExitCode: null } &&
        found["signaled"].LastExitCode!.GetValue<string>() == "terminated by signal" &&
        found["unloaded"] is { Registered: false, Supervised: false, State: "not_loaded" }, "launchctl states and exit codes");
    Check(before.SequenceEqual(Entries(locations.Agents)), "inspect leaves the agents directory intact");
    File.WriteAllText(Path.Combine(locations.Agents, instance.CaptureAgentLabel("broken") + ".plist"), "");
    try { await jobs.Inspect([], default); }
    catch (IOException error) when (error.Message.Contains("print failed")) { return; }
    throw new Exception("launchctl failure did not propagate");
}
finally { temp.Delete(recursive: true); }

static void WriteExecutable(string path, string text)
{
    File.WriteAllText(path, text);
    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
}

static string[] Entries(string path) => Directory.GetFileSystemEntries(path).Order(StringComparer.Ordinal).ToArray();

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
