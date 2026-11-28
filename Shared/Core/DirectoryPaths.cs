namespace Orisumi.Core;

internal static class DirectoryPaths
{
    internal static string Resolve(string path)
    {
        var directory = new DirectoryInfo(path);
        if (directory.Parent is not { } parent) return directory.FullName;
        var resolved = new DirectoryInfo(Path.Combine(Resolve(parent.FullName), directory.Name));
        if (resolved.LinkTarget is null) return resolved.FullName;
        if (resolved.ResolveLinkTarget(returnFinalTarget: true) is not { Exists: true } target)
            throw new IOException($"无法解析目录：{path}");
        return Resolve(target.FullName);
    }

    internal static bool Contains(string parent, string path)
    {
        var relative = Path.GetRelativePath(parent, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }
}
