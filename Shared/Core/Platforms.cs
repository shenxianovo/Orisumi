namespace Orisumi.Core;

internal static partial class Platforms
{
    internal static string Current => Name(CurrentRid);

    internal static string Name(string rid) => rid switch
    {
        "osx-arm64" => "macos",
        "win-x64" => "windows",
        _ => throw new ArgumentException($"未知的目标 RID：{rid}。", nameof(rid)),
    };

    internal static bool Runs(string component, string rid) =>
        component is not ("macos" or "windows") || component == Name(rid);
}
