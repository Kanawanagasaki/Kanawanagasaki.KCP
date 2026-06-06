namespace Kanawanagasaki.KCP;

using System.Buffers;

public sealed class PooledMemory : IDisposable
{
    private byte[]? _array;
    private int _length;
    private bool _disposed;

    internal Span<byte> Span
        => _array.AsSpan(0, _length);

    internal ReadOnlyMemory<byte> Memory
        => _array.AsMemory(0, _length);

    internal PooledMemory(ReadOnlySpan<byte> data)
    {
        _array = ArrayPool<byte>.Shared.Rent(data.Length);
        _length = data.Length;
        data.CopyTo(_array);
    }

    internal PooledMemory(int size)
    {
        _array = ArrayPool<byte>.Shared.Rent(size);
        _length = size;
    }

    internal void SetLength(int length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_array is null)
            _array = ArrayPool<byte>.Shared.Rent(length);
        else if (_array.Length < length)
        {
            var newArr = ArrayPool<byte>.Shared.Rent(length);
            _array.AsSpan(0, _length).CopyTo(newArr);
            ArrayPool<byte>.Shared.Return(_array);
            _array = newArr;
        }

        _length = length;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        if (_array is not null)
        {
            ArrayPool<byte>.Shared.Return(_array);
            _array = null;
        }

        _length = 0;
        _disposed = true;
    }
}
