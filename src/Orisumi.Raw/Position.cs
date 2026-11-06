using System.Globalization;

namespace Orisumi.Raw;

public sealed record Position(string Source, BlockName Capture, string Path, int Line)
{
    public override string ToString() => $"{Source}/{Capture}/{Path}:{Line.ToString(CultureInfo.InvariantCulture)}";

    public string Display => $"{Source}/{Capture.Abbreviation}/{Path}:{Line.ToString(CultureInfo.InvariantCulture)}";

    internal static (string Source, string Capture, string Path, int Line) Split(string text)
    {
        var colon = text.LastIndexOf(':');
        var parts = colon < 0 ? [] : text[..colon].Split('/', 3);
        if (parts.Length < 3 || !int.TryParse(text[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var line) || line < 1)
            throw new FormatException($"原件位置格式有误：{text}");
        return (parts[0], parts[1], parts[2], line);
    }
}
