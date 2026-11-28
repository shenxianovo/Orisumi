using System.Text;

namespace Orisumi.Core;

internal static partial class ChromeExtension
{
    private static partial byte[] PathBytes(string absolutePath) => Encoding.UTF8.GetBytes(absolutePath);
}
