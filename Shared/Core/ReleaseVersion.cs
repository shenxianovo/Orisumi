namespace Orisumi.Core;

internal static class ReleaseVersion
{
    internal static Version? Parse(string value)
    {
        if (value.StartsWith('v')) value = value[1..];
        return Version.TryParse(value, out var version) && version.Build >= 0 && version.Revision < 0 &&
            version.ToString() == value ? version : null;
    }

    internal static Version Require(string value) => Parse(value) ??
        throw new ArgumentException($"无效的发布版本：{value}，请使用 5.2.0 或 v5.2.0 格式。");

    internal static string AssetPrefix(Version version, string rid) => $"orisumi-{version}-{rid}";
}
