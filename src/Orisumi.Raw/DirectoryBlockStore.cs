namespace Orisumi.Raw;

public sealed class DirectoryBlockStore(string directory) : IBlockStore
{
    public string PathOf(BlockName name) => Path.Combine(directory, name.Hex[..2], name.ToString());

    public (BlockName Name, bool Added) Put(ReadOnlySpan<byte> content)
    {
        var name = BlockName.Of(content);
        var path = PathOf(name);
        if (File.Exists(path)) return (name, false);
        return (name, StoreFile.Create(path, BlockCodec.Encode(content)));
    }

    public byte[] Read(BlockName name)
    {
        var content = BlockCodec.Decode(File.ReadAllBytes(PathOf(name)));
        if (BlockName.Of(content) != name) throw new InvalidDataException($"块 {name} 的内容与块名不符");
        return content;
    }

    public IEnumerable<BlockName> Names()
    {
        if (!Directory.Exists(directory)) yield break;
        foreach (var file in Directory.EnumerateFiles(directory, $"{BlockName.Algorithm}-*", SearchOption.AllDirectories))
            if (BlockName.TryParse(Path.GetFileName(file), out var name)) yield return name;
    }
}
