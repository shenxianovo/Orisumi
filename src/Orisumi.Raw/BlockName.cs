using System.Security.Cryptography;

namespace Orisumi.Raw;

public readonly record struct BlockName
{
    public const string Algorithm = "sha256";

    public const int AbbreviationLength = 12;

    private const string Prefix = Algorithm + "-";

    private const int HexLength = SHA256.HashSizeInBytes * 2;

    private BlockName(string hex) => Hex = hex;

    public string Hex { get; }

    public string Abbreviation => Hex[..AbbreviationLength];

    public static BlockName Of(ReadOnlySpan<byte> content) => new(Convert.ToHexStringLower(SHA256.HashData(content)));

    public static BlockName FromDigest(ReadOnlySpan<byte> digest) => new(Convert.ToHexStringLower(digest));

    public static BlockName Parse(string text) =>
        TryParse(text, out var name) ? name : throw new FormatException($"块名格式有误：{text}");

    public static bool TryParse(string? text, out BlockName name)
    {
        name = default;
        if (text is null || !text.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var hex = text[Prefix.Length..];
        if (!IsLowerHex(hex) || hex.Length != HexLength) return false;
        name = new BlockName(hex);
        return true;
    }

    public static bool IsLowerHex(string text) => text.All(character => char.IsAsciiHexDigitLower(character) || char.IsAsciiDigit(character));

    public override string ToString() => Prefix + Hex;

    public static string StripAlgorithm(string text) =>
        text.StartsWith(Prefix, StringComparison.Ordinal) ? text[Prefix.Length..] : text;
}
