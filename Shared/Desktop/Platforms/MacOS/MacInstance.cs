namespace Orisumi.Core;

internal static class MacInstance
{
    extension(Instance instance)
    {
        internal static string BundleIdentifierFor(string name) => name == "prod" ? "dev.orisumi" : "dev.orisumi.canary";
        internal static string AppNameFor(string name) => name == "prod" ? "Orisumi" : "Orisumi Dev";
        internal string BundleIdentifier => Instance.BundleIdentifierFor(instance.Name);
        internal string AppAgentLabel => $"dev.orisumi.{instance.Name}.app";
        internal string CaptureAgentPrefix => $"dev.orisumi.{instance.Name}.capture.";
        internal string CaptureAgentLabel(string source) => instance.CaptureAgentPrefix + source;
        internal string? CommandName => instance.Name == "prod" ? "orisumi" : null;
    }
}
