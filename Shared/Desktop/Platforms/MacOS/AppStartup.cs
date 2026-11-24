using System.Runtime.Versioning;
using System.Xml.Linq;
using Orisumi.Platform;

namespace Orisumi.Desktop;

[SupportedOSPlatform("macos")]
internal static class AppStartup
{
    internal static async Task Install(Instance instance, SystemLocations locations, bool loaded, CancellationToken token)
    {
        var label = instance.AppAgentLabel;
        var service = Launchd.Service(label);
        var path = Path.Combine(locations.Agents, label + ".plist");
        var plist = Launchd.Plist(instance, new XElement("dict",
            new XElement("key", "Label"), new XElement("string", label),
            new XElement("key", "ProgramArguments"), new XElement("array",
                new XElement("string", HostBundle.AppExecutable(instance.Store.Host)), new XElement("string", "--menu-bar")),
            new XElement("key", "RunAtLoad"), new XElement("true")));
        Directory.CreateDirectory(locations.Agents);
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            plist.Save(temporary);
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }

        if (!loaded)
            await Launchd.Bootstrap(locations, path, token);
        else
        {
            var result = await Launchd.Run(locations, token, "kickstart", "-k", service);
            if (result.Code != 0) throw Launchd.Error("kickstart", service, result.Error);
        }
    }
}
