#!/usr/bin/env dotnet
#:property PublishAot=false

using System.Diagnostics;
using System.Text.Json;

var directory = AppContext.BaseDirectory;
var name = Path.GetFileNameWithoutExtension(Environment.ProcessPath);
var remote = Path.Combine(directory, "remote");
switch (name, args)
{
    case ("publish-fixture", ["msbuild", ..]):
        using (var msbuild = Process.Start(new ProcessStartInfo(File.ReadAllText(Path.Combine(directory, "dotnet-path")), args)
            { UseShellExecute = false })!)
        {
            await msbuild.WaitForExitAsync();
            return msbuild.ExitCode;
        }
    case ("publish-fixture", ["publish", var project, "-c", "Release", "-r", var rid, "-p:PublishAot=true",
        var informational, "-p:IncludeSourceRevisionInInformationalVersion=false", "-p:TreatWarningsAsErrors=true", "-warnaserror", "-tl:off", "-o", var target]):
        var component = Path.GetFileNameWithoutExtension(project).Split('.')[^1].ToLowerInvariant();
        if (component == "cli") component = "host";
        File.AppendAllText(Path.Combine(directory, "publishes"), target + "\n");
        if (component == "codex" && File.Exists(Path.Combine(directory, "fail-publish"))) return 17;
        Directory.CreateDirectory(target);
        var executable = component == "host" ? "orisumi" : "orisumi-" + component;
        File.Copy(Environment.ProcessPath!, Path.Combine(target, executable + (rid == "win-x64" ? ".exe" : "")));
        foreach (var file in Directory.GetFiles(directory, "FixtureTool.*"))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        var version = informational["-p:InformationalVersion=".Length..];
        File.WriteAllText(Path.Combine(target, "describe.json"), JsonSerializer.Serialize(new
        {
            name = component, version, arguments = component == "host" ? null : new[] { "capture" },
        }));
        if (component != "host") File.Copy(Path.Combine(directory, $"schema-{component}.json"), Path.Combine(target, "schema.json"));
        return 0;
    case ("npm", ["ci"]) when File.Exists("manifest.json"):
        return 0;
    case ("npm", ["run", "build", "--", "--outDir", var output]) when File.Exists("manifest.json"):
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "worker.js"), "extension\n");
        return 0;
    case ("gh", ["release", "download", var tag, "--pattern", var asset, "--dir", var download]):
        Event("download " + tag);
        File.Copy(Path.Combine(remote, tag, asset), Path.Combine(download, asset));
        return 0;
    case ("gh", ["release", "create", var tag, "--target", var commit, "--title", var title, "--notes", var notes,
        var package, var manifest]) when tag == title && commit == File.ReadAllText(Path.Combine(remote, "head")):
        Event("create " + tag);
        var release = Directory.CreateDirectory(Path.Combine(remote, tag)).FullName;
        foreach (var file in new[] { package, manifest }) File.Copy(file, Path.Combine(release, Path.GetFileName(file)));
        File.WriteAllText(Path.Combine(release, "notes"), notes);
        return 0;
    case (_, ["describe"]):
    case (_, ["schema"]):
        Console.Write(File.ReadAllText(Path.Combine(directory, args[0] + ".json")));
        return 0;
    default:
        Console.Error.WriteLine($"Unexpected fixture invocation: {name} {string.Join(' ', args)}");
        return 2;
}

void Event(string line) => File.AppendAllText(Path.Combine(remote, "events"), line + "\n");
