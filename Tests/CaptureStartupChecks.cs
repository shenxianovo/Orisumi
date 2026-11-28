#!/usr/bin/env dotnet
#:project ../Host/Cli/Orisumi.Cli.csproj
#:include Fixtures/MemoryJobs.cs
#:property AssemblyName=CaptureStartupChecks
#:property PublishAot=false

using Orisumi.Cli;
using Orisumi.Cli.Capture;
using Orisumi.Core;

var temp = Directory.CreateTempSubdirectory("orisumi-startup-");
try
{
    var instance = Instance.Create(Path.Combine(temp.FullName, "store & spaces ' 引号"), "test_startup");
    var store = instance.Store;
    var host = Path.GetFullPath("Host/Cli/bin/Debug/net11.0");
    var programs = await CollectorPrograms.Read(store.Root, default, host);
    string[] expected = ["browser", "claude", "codex", Platforms.Current];
    Check(programs.Select(program => program.Description.Name).Order().SequenceEqual(expected), "collector discovery");
    var extension = ChromeExtension.DirectoryFor(instance);
    Directory.CreateDirectory(extension);
    File.WriteAllText(Path.Combine(extension, "manifest.json"), """{"name":"fixture"}""");
    ChromeExtension.Configure(extension, instance);

    var jobs = new MemoryJobs();
    await CaptureStartup.Start(instance, jobs, TextWriter.Null, default, host);
    foreach (var program in programs)
    {
        var name = program.Description.Name;
        var job = jobs.Jobs[name].Spec!;
        Check(job.Command.SequenceEqual([RunProcess.Dotnet, program.Path, "--store", store.Root, .. program.Description.Arguments!]) &&
            job.WorkingDirectory == store.Root && job.StandardOutput == Path.Combine(store.Logs, name + ".out.log") &&
            job.StandardError == Path.Combine(store.Logs, name + ".err.log"), "job command and paths for " + name);
    }
    await CaptureStartup.Stop(instance, jobs, TextWriter.Null, default);
    Check(jobs.Jobs.Values.All(job => job.Spec is null), "all collectors stopped");
}
finally { temp.Delete(true); }

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
