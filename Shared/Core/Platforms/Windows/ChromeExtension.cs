using System.Runtime.InteropServices;

namespace Orisumi.Core;

internal static partial class ChromeExtension
{
    private static partial byte[] PathBytes(string absolutePath)
    {
        if (!Path.IsPathFullyQualified(absolutePath)) throw new ArgumentException("需要扩展的完整路径。", nameof(absolutePath));
        // Chromium's MaybeNormalizePath changes only an ASCII drive letter.
        if (absolutePath is [>= 'a' and <= 'z', ':', ..])
            absolutePath = char.ToUpperInvariant(absolutePath[0]) + absolutePath[1..];
        return MemoryMarshal.AsBytes(absolutePath.AsSpan()).ToArray();
    }
}
