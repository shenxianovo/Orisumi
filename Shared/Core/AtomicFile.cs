namespace Orisumi.Core;

/// Replaces a file with another in one step, also while other processes have it open for reading
/// (with FileShare.Delete): readers keep the old content, later opens see the new one.
public static partial class AtomicFile
{
    public static partial void Replace(string source, string destination);
}
