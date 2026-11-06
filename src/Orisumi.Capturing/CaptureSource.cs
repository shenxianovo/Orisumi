namespace Orisumi.Capturing;

public sealed record CaptureSource(string Name, string Origin, IReadOnlyList<FileRoot> Roots)
{
    public IReadOnlyList<FileInput> Files { get; init; } = [];
}

public sealed record FileRoot(string Directory, string Prefix = "", Func<string, bool>? Include = null, bool Optional = false);

public sealed record FileInput(string SourcePath, string Path, FileInputMethod Method = FileInputMethod.FileBytes, bool Optional = false);

public enum FileInputMethod
{
    FileBytes,
    SqliteBackup,
}
