namespace Orisumi.Raw;

internal sealed class BlockStream(IBlockStore blocks, IReadOnlyList<BlockName> names) : Stream
{
    private byte[] _current = [];
    private int _offset;
    private int _next;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty) return 0;
        while (_offset == _current.Length)
        {
            if (_next == names.Count) return 0;
            _current = blocks.Read(names[_next]);
            _next++;
            _offset = 0;
        }
        var length = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsSpan(_offset, length).CopyTo(buffer);
        _offset += length;
        return length;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
