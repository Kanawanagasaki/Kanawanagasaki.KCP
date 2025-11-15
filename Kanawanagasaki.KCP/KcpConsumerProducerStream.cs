namespace Kanawanagasaki.KCP;

using System.Buffers;
using System.Threading.Channels;

public class KcpConsumerProducerStream : Stream
{
    private readonly KcpTransport _transport;
    private readonly Channel<ReadOnlyMemory<byte>> _receiveChannel;
    private byte[]? _currentBuffer;
    private int _currentBufferOffset;
    private int _currentBufferRentedLength;
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
        _receiveChannel = Channel.CreateBounded<ReadOnlyMemory<byte>>(_channelOptions);
    }

    internal async ValueTask WriteReceivedDataAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (_isDisposed || data.IsEmpty)
            return;

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
                await Task.Delay(Math.Clamp((int)_transport.Interval / 2, 5, 1000), ct);
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
            if (_currentBuffer is not null)
            {
                int available = _currentBufferRentedLength - _currentBufferOffset;
                int toCopy = Math.Min(available, buffer.Length);
                _currentBuffer.AsSpan(_currentBufferOffset, toCopy).CopyTo(buffer.Span);
                _currentBufferOffset += toCopy;

                if (_currentBufferRentedLength <= _currentBufferOffset)
                {
                    ArrayPool<byte>.Shared.Return(_currentBuffer);
                    _currentBuffer = null;
                    _currentBufferOffset = 0;
                    _currentBufferRentedLength = 0;
                }

                bytesRead = toCopy;
            }
        }

        if (buffer.Length <= bytesRead)
            return bytesRead;

        while (bytesRead < buffer.Length)
        {
            if (_receiveChannel.Reader.TryRead(out var bufferItem))
            {
                int toCopy = Math.Min(bufferItem.Length, buffer.Length - bytesRead);

                if (toCopy == bufferItem.Length)
                {
                    bufferItem.Span.CopyTo(buffer.Span.Slice(bytesRead));
                    bytesRead += toCopy;
                }
                else
                {
                    bufferItem.Span.Slice(0, toCopy).CopyTo(buffer.Span.Slice(bytesRead));
                    bytesRead += toCopy;

                    int excessLength = bufferItem.Length - toCopy;
                    byte[]? tempBuffer = null;
                    try
                    {
                        lock (_syncLock)
                        {
                            if (_isDisposed)
                                break;

                            tempBuffer = ArrayPool<byte>.Shared.Rent(excessLength);
                            bufferItem.Span.Slice(toCopy).CopyTo(tempBuffer);

                            _currentBuffer = tempBuffer;
                            _currentBufferOffset = 0;
                            _currentBufferRentedLength = excessLength;
                            tempBuffer = null;
                        }
                    }
                    finally
                    {
                        if (tempBuffer is not null)
                            ArrayPool<byte>.Shared.Return(tempBuffer);
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

        int result = _transport.Write(buffer.AsMemory(offset, count));

        if (result < 0)
            throw new IOException($"KCP write failed with error code {result}");
        if (result != buffer.Length)
            throw new IOException("Partial writes not supported in stream mode");
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

            if (_currentBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(_currentBuffer);
                _currentBuffer = null;
                _currentBufferOffset = 0;
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

            if (_currentBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(_currentBuffer);
                _currentBuffer = null;
                _currentBufferOffset = 0;
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}
