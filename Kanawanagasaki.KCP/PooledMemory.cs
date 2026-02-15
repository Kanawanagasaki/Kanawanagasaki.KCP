namespace Kanawanagasaki.KCP;

using System.Buffers;

internal sealed class PooledMemory : IDisposable
{
    private readonly IMemoryOwner<byte> _memoryOwner;
    private bool _disposed = false;

    internal Memory<byte> Memory { get; private set; }

    internal PooledMemory(IMemoryOwner<byte> memoryOwner, int size)
    {
        _memoryOwner = memoryOwner ?? throw new ArgumentNullException(nameof(memoryOwner));
        Memory = memoryOwner.Memory.Slice(0, size);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _memoryOwner.Dispose();
            Memory = default;
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
