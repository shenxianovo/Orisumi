namespace Orisumi.Raw;

public interface IBlockStore
{
    (BlockName Name, bool Added) Put(ReadOnlySpan<byte> content);

    byte[] Read(BlockName name);

    IEnumerable<BlockName> Names();
}
