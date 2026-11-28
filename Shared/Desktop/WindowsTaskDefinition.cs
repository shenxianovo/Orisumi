using System.Xml.Linq;

namespace Orisumi.Desktop;

internal static class WindowsTaskDefinition
{
    internal static XDocument Create(string user, string executable, string store)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement Element(string name, params object[] contents) => new(ns + name, contents);
        return new XDocument(new XDeclaration("1.0", "utf-16", null),
            Element("Task", new XAttribute("version", "1.2"),
                Element("Triggers", Element("LogonTrigger", Element("UserId", user))),
                Element("Principals", Element("Principal", new XAttribute("id", "User"),
                    Element("UserId", user), Element("LogonType", "InteractiveToken"), Element("RunLevel", "LeastPrivilege"))),
                Element("Settings", Element("MultipleInstancesPolicy", "IgnoreNew"),
                    Element("DisallowStartIfOnBatteries", false), Element("StopIfGoingOnBatteries", false),
                    Element("ExecutionTimeLimit", "PT0S"),
                    Element("RestartOnFailure", Element("Interval", "PT1M"), Element("Count", 999))),
                Element("Actions", new XAttribute("Context", "User"),
                    Element("Exec", Element("Command", executable), Element("Arguments", $"--store \"{store}\"")))));
    }
}
