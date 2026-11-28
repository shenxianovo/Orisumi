namespace Orisumi.Core;

public static partial class AtomicFile
{
    // rename(2) replaces atomically regardless of open readers.
    public static partial void Replace(string source, string destination) => File.Move(source, destination, overwrite: true);
}
