using System.Xml.Linq;

namespace Orisumi.Desktop;

internal static class WindowsTaskDefinition
{
    private static readonly XNamespace Namespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private static XElement Element(string name, params object?[] contents) => new(Namespace + name, contents);

    internal static XDocument Capture(string user, string executable, string store) =>
        Create(user, executable, Element("Arguments", $"--store \"{store}\""),
            Element("RestartOnFailure", Element("Interval", "PT1M"), Element("Count", 999)));

    internal static XDocument App(string user, string executable) => Create(user, executable, null, null);

    private static XDocument Create(string user, string executable, XElement? arguments, XElement? restart)
    {
        return new XDocument(new XDeclaration("1.0", "utf-16", null),
            Element("Task", new XAttribute("version", "1.2"),
                Element("Triggers", Element("LogonTrigger", Element("UserId", user))),
                Element("Principals", Element("Principal", new XAttribute("id", "User"),
                    Element("UserId", user), Element("LogonType", "InteractiveToken"), Element("RunLevel", "LeastPrivilege"))),
                Element("Settings", Element("MultipleInstancesPolicy", "IgnoreNew"),
                    Element("DisallowStartIfOnBatteries", false), Element("StopIfGoingOnBatteries", false),
                    Element("ExecutionTimeLimit", "PT0S"), restart),
                Element("Actions", new XAttribute("Context", "User"),
                    Element("Exec", Element("Command", executable), arguments))));
    }
}
