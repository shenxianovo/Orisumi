namespace Orisumi.Capturing;

internal static class ClaudeCodeCapture
{
    public static Capture Run(Store store, string directory, TimeRange range)
    {
        var root = Path.GetFullPath(directory);
        var writer = store.BeginCapture("claude-code", root, range);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            writer.Copy(file, Path.GetRelativePath(root, file));
        return writer.Complete();
    }
}
