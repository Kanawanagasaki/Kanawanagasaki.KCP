namespace Kanawanagasaki.KCP;

using System.Buffers;
using System.Threading.Channels;

public class KcpConsumerProducerStream : Stream
{
    private readonly KcpTransport _transport;
    private readonly Channel<PooledMemory> _receiveChannel;
    private PooledMemory? _currentPooled;
    private int _currentPooledOffset;
    private bool _isDisposed;
    private readonly Lock _syncLock = new();

    private static readonly BoundedChannelOptions _channelOptions = new(1000)
    {
        FullMode = BoundedChannelFullMode.Wait
    };

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public KcpConsumerProducerStream(KcpTransport transport)
    {
        _transport = transport;
        _receiveChannel = Channel.CreateBounded<PooledMemory>(_channelOptions);
    }

    internal async ValueTask WriteReceivedDataAsync(PooledMemory data, CancellationToken ct)
    {
        if (_isDisposed || data.Memory.IsEmpty)
        {
            data.Dispose();
            return;
        }

        await _receiveChannel.Writer.WriteAsync(data, ct).ConfigureAwait(false);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (buffer.IsEmpty)
            return;

        int offset = 0;
        while (offset < buffer.Length)
        {
            var threeQuarters = Math.Ceiling(_transport.SendWindow / 4.0 * 3.0);
            if (threeQuarters <= _transport.GetWaitSnd())
            {
                _transport.Flush();
                await _transport.WaitForSendWindowAsync(ct);
            }
            else
            {
                var toCopy = (int)Math.Min(buffer.Length - offset, _transport.Mtu - KcpConstants.IKCP_OVERHEAD);
                var res = _transport.Write(buffer.Slice(offset, toCopy));

                if (res < 0)
                    throw new IOException($"KCP write failed with error code {res}");

                offset += toCopy;
            }
        }

        _transport.Flush();
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct = default)
        => await ReadAsync(buffer.AsMemory(offset, count), ct);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (buffer.IsEmpty)
            return 0;

        int bytesRead = 0;

        lock (_syncLock)
        {
            if (_currentPooled is not null)
            {
                int available = _currentPooled.Memory.Length - _currentPooledOffset;
                int toCopy = Math.Min(available, buffer.Length);
                _currentPooled.Memory.Span.Slice(_currentPooledOffset, toCopy).CopyTo(buffer.Span);
                _currentPooledOffset += toCopy;
                bytesRead = toCopy;

                if (_currentPooled.Memory.Length <= _currentPooledOffset)
                {
                    _currentPooled.Dispose();
                    _currentPooled = null;
                    _currentPooledOffset = 0;
                }
            }
        }

        if (buffer.Length <= bytesRead)
            return bytesRead;

        while (bytesRead < buffer.Length)
        {
            if (_receiveChannel.Reader.TryRead(out var pooledItem))
            {
                int toCopy = Math.Min(pooledItem.Memory.Length, buffer.Length - bytesRead);

                if (toCopy == pooledItem.Memory.Length)
                {
                    pooledItem.Memory.Span.CopyTo(buffer.Span.Slice(bytesRead));
                    bytesRead += toCopy;
                    pooledItem.Dispose();
                }
                else
                {
                    pooledItem.Memory.Span.Slice(0, toCopy).CopyTo(buffer.Span.Slice(bytesRead));
                    bytesRead += toCopy;

                    lock (_syncLock)
                    {
                        if (_isDisposed)
                        {
                            pooledItem.Dispose();
                            break;
                        }

                        _currentPooled = pooledItem;
                        _currentPooledOffset = toCopy;
                    }
                }
            }
            else
            {
                if (0 < bytesRead)
                    return bytesRead;

                if (!await _receiveChannel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                    break;
            }
        }

        return bytesRead;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ThrowIfDisposed();
        if (count == 0)
            return;

        count = Math.Min(count, buffer.Length);

        if (_transport.GetFreeSendWindowBytes() < count)
            throw new SendWindowExceededException("Write failed, send window is full");

        var span = buffer.AsMemory(offset, count);
        while (!span.IsEmpty)
        {
            int result = _transport.Write(span);
            if (result < 0)
                throw new IOException($"KCP write failed with error code {result}");
            span = span.Slice(result);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException("Synchronous reads are not supported on this stream.");
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(KcpConsumerProducerStream));
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed)
            return;

        lock (_syncLock)
        {
            _isDisposed = true;

            try
            {
                _receiveChannel.Writer.Complete();
            }
            catch { }

            while (_receiveChannel.Reader.TryRead(out var item))
                item.Dispose();

            if (_currentPooled is not null)
            {
                _currentPooled.Dispose();
                _currentPooled = null;
                _currentPooledOffset = 0;
            }
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_isDisposed)
            return;

        lock (_syncLock)
        {
            _isDisposed = true;

            try
            {
                _receiveChannel.Writer.Complete();
            }
            catch { }

            while (_receiveChannel.Reader.TryRead(out var item))
                item.Dispose();

            if (_currentPooled is not null)
            {
                _currentPooled.Dispose();
                _currentPooled = null;
                _currentPooledOffset = 0;
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}
