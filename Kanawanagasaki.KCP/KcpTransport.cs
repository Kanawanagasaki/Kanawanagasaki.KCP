namespace Kanawanagasaki.KCP;

using System.Threading.Channels;

public abstract class KcpTransport : IAsyncDisposable, IDisposable
{
    private readonly KcpConversation _kcp;
    private readonly SemaphoreSlim _sendSemaphore = new(1, 1);
    private readonly Lock _syncLock = new();

    private CancellationTokenSource? _cts;
    private Channel<ReadOnlyMemory<byte>>? _receiveChannel;

    private Task? _processingTask;
    private volatile bool _disposed = false;
    private volatile bool _isRunning = false;

    private KcpConsumerProducerStream _stream;

    /// <summary>
    /// Raised when internal KCP events generate log messages.
    /// </summary>
    public event Action<string>? OnLogMessage;

    /// <summary>
    /// Unique conversation ID for this KCP session.
    /// </summary>
    public uint ConversationId => _kcp.Conv;

    public bool NoDelay => _kcp.NoDelay != 0;
    public uint Interval => _kcp.Interval;
    public uint SendWindow => _kcp.SendWindow;
    public uint ReceiveWindow => _kcp.ReceiveWindow;
    public uint RemoteWindow => _kcp.RemoteWindow;
    public uint Mtu => _kcp.Mtu;
    public bool IsStreamMode => 0 < _kcp.StreamMode;

    /// <summary>
    /// Indicates if the transport is actively processing data.
    /// Returns false after disposal or explicit stop.
    /// </summary>
    public bool IsRunning => _isRunning && !_disposed;

    /// <summary>
    /// Initializes a new KCP transport session.
    /// </summary>
    /// <param name="conversationId">Unique session identifier</param>
    protected KcpTransport(uint conversationId)
    {
        _kcp = new KcpConversation(conversationId);
        _stream = new KcpConsumerProducerStream(this);
        ConfigureOutput();
    }

    /// <summary>
    /// Abstract method that implementations must provide to send data through their transport.
    /// This is called by KCPTransport when it has data ready to send.
    /// </summary>
    /// <param name="data">KCP-formatted packet data</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Number of bytes sent, or negative value on error</returns>
    protected abstract ValueTask<int> SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>
    /// Input data received from the transport layer.
    /// This should be called by the transport when data is received.
    /// </summary>
    /// <param name="data">KCP-formatted packet data</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Number of bytes processed</returns>
    public void Input(ReadOnlyMemory<byte> data)
    {
        ThrowIfDisposed();

        if (data.IsEmpty)
            return;

        lock (_syncLock)
        {
            var result = _kcp.Input(data.Span);
            if (result < 0)
                throw new KcpException("Operation failed with error code: " + result);
        }
    }

    /// <summary>
    /// Queues application data for reliable transmission through KCP.
    /// Data is fragmented, sequenced, and retransmitted as needed.
    /// </summary>
    /// <param name="data">Application payload to send</param>
    /// <returns>Number of bytes queued</returns>
    public int Write(ReadOnlyMemory<byte> data)
    {
        if (data.IsEmpty)
            return 0;

        lock (_syncLock)
        {
            ThrowIfDisposed();

            if ((int)_kcp.SendWindow <= _kcp.WaitSnd())
                throw new SendWindowExceededException("Write failed, send window is full");

            var sendRes = _kcp.Send(data.Span);

            return sendRes switch
            {
                -1 => throw new SegmentSizeExceededException("Write failed, segment size exceeds maximum allowed"),
                -2 => throw new SendWindowExceededException("Write failed, send window is full"),
                < 0 => throw new IOException($"Write failed, unexpected error code: {sendRes}"),
                _ => sendRes
            };
        }
    }

    /// <summary>
    /// Reads the next reconstructed application packet from receive buffer.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Reconstructed data</returns>
    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (IsStreamMode)
            throw new InvalidOperationException("In stream mode, obtain the stream using GetStream() and read data from it");

        if (_receiveChannel is null)
            return Array.Empty<byte>();

        return await _receiveChannel.Reader.ReadAsync(ct).ConfigureAwait(false);
    }

    public Stream GetStream()
        => _stream;

    /// <summary>
    /// Peek the size of the next available packet without dequeuing.
    /// </summary>
    /// <returns>Packet size in bytes, or -1 if no packet available</returns>
    public int PeekSize()
    {
        lock (_syncLock)
        {
            ThrowIfDisposed();
            return _kcp.PeekSize();
        }
    }

    /// <summary>
    /// Starts the KCP processing background task.
    /// Must be called before sending/receiving data.
    /// </summary>
    public void Start()
    {
        ThrowIfDisposed();

        lock (_syncLock)
        {
            if (_isRunning)
                return;
            _isRunning = true;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        _receiveChannel?.Writer.Complete();
        _receiveChannel = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

        _processingTask = ProcessAsync();
    }

    /// <summary>
    /// Stops processing.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (!_isRunning || _disposed)
            return;

        _isRunning = false;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        _receiveChannel?.Writer.Complete();
        _receiveChannel = null;

        if (_processingTask is not null)
        {
            try
            {
                await _processingTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                OnLogMessage?.Invoke(e.Message);
            }
        }
    }

    private void ConfigureOutput()
    {
        _kcp.SetOutput((data, kcp, user) =>
        {
            var memory = data.ToArray().AsMemory();

            _ = Task.Run(async () =>
            {
                try
                {
                    await SendAsync(memory, _cts?.Token ?? default).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    OnLogMessage?.Invoke($"Packet send failed: {e.Message}");
                }
            }, _cts?.Token ?? default);

            return memory.Length;
        });

        _kcp.SetWriteLog((log, kcp, user) =>
        {
            OnLogMessage?.Invoke(log);
        });
    }

    /// <summary>
    /// Immediately flushes all pending send packets.
    /// </summary>
    public void Flush()
    {
        ThrowIfDisposed();

        lock (_syncLock)
            _kcp.Flush();
    }

    /// <summary>
    /// Gets count of unacknowledged packets in send queue.
    /// </summary>
    /// <returns>Number of pending packets</returns>
    public int GetWaitSnd()
    {
        ThrowIfDisposed();
        return _kcp.WaitSnd();
    }

    /// <summary>
    /// Configures KCP timing and reliability parameters.
    /// 
    /// noDelay:
    ///   False = Normal mode: ACKs are delayed by interval to combine multiple ACKs
    ///   True = No-delay mode: ACKs are sent immediately (reduces latency but increases packet count)
    ///   
    /// intervalMs:
    ///   Internal timer resolution in milliseconds. Lower values reduce latency but increase CPU usage.
    ///   
    /// fastResend:
    ///   Fast retransmission threshold. When set to N (N>0), KCP will resend a packet after receiving N duplicate ACKs.
    ///   0 = Disable fast retransmit (use standard RTO-based retransmission only)
    ///   2 = Recommended for most real-time applications (resend after 2 duplicate ACKs)
    ///   
    /// noCongestionControl:
    ///   False = reduces window size when packet loss detected
    ///   True = aggressive packets sending
    ///   
    /// Gaming/Low-latency profile recommendation:
    ///   SetNoDelay(true, 10, 2, true)  // No delay, 10ms interval, fast resend=2, no congestion control
    /// 
    /// General purpose profile recommendation:
    ///   SetNoDelay(false, 100, 0, false) // Standard delay, 100ms interval, no fast resend, with congestion control
    /// </summary>
    /// <param name="noDelay">Disable ACK delay for lower latency</param>
    /// <param name="intervalMs">Internal update interval in milliseconds</param>
    /// <param name="fastResend">Enable fast retransmit after N duplicate ACKs</param>
    /// <param name="noCongestionControl">Enable bandwidth estimation</param>
    /// <returns>0 on success, negative on error</returns>
    public void SetNoDelay(bool noDelay, int intervalMs, int fastResend, bool noCongestionControl)
    {
        ThrowIfDisposed();

        lock (_syncLock)
            _kcp.SetNoDelay(noDelay ? 1 : 0, intervalMs, fastResend, noCongestionControl ? 1 : 0);
    }

    /// <summary>
    /// Sets send and receive window sizes for flow control.
    /// 
    /// sndwnd: Maximum number of unacknowledged packets allowed (send window)
    /// rcvwnd: Maximum number of out-of-order packets to buffer (receive window)
    /// 
    /// Default values: 
    ///   sendWindow = 32, reciveWindow = 32
    /// 
    /// For high-latency networks, increase both values proportionally to bandwidth-delay product.
    /// </summary>
    /// <param name="sendWindow">Send window size in packets</param>
    /// <param name="receiveWindow">Receive window size in packets</param>
    /// <returns>0 on success</returns>
    public void SetWindowSize(int sendWindow, int receiveWindow)
    {
        ThrowIfDisposed();

        lock (_syncLock)
            _kcp.SetWndSize(sendWindow, receiveWindow);
    }

    /// <summary>
    /// Set MTU size.
    /// </summary>
    /// <param name="mtu">Maximum transmission unit size</param>
    /// <returns>0 on success</returns>
    public void SetMtu(int mtu)
    {
        ThrowIfDisposed();

        lock (_syncLock)
        {
            var res = _kcp.SetMtu(mtu);
            if (res == 0)
                return;

            if (res == -1)
                throw new ArgumentOutOfRangeException($"The specified MTU value ({mtu}) is too small");
            else if (res == -2)
                throw new OutOfMemoryException("Failed to allocate internal buffer for the new MTU size");
            else
                throw new InvalidOperationException($"An unexpected error occurred while setting MTU. Error code: {res}");
        }
    }

    /// <summary>
    /// Enables stream mode for continuous byte stream delivery.
    /// 
    /// When disabled (default):
    ///   - Preserves message boundaries
    ///   - Each Send() corresponds to one Recv() call
    ///   - Ideal for discrete messages
    /// 
    /// When enabled:
    ///   - Treats data as continuous byte stream
    ///   - No message boundaries preserved
    ///   - Ideal for streaming data
    /// </summary>
    /// <param name="enable">Enable stream mode</param>
    public void SetStreamMode(bool enable)
    {
        ThrowIfDisposed();

        lock (_syncLock)
            _kcp.SetStreamMode(enable);
    }

    /// <summary>
    /// Sets the internal update interval for KCP state machine.
    /// This is a lower-level alternative to the UpdateInterval property.
    /// 
    /// Note: This directly maps to the KCP's internal interval setting.
    /// For most applications, setting the UpdateInterval property is sufficient.
    /// </summary>
    /// <param name="interval">Interval in milliseconds</param>
    /// <returns>0 on success</returns>
    public void SetInterval(int interval)
    {
        ThrowIfDisposed();

        lock (_syncLock)
            _kcp.SetInterval(interval);
    }

    private async Task ProcessAsync()
    {
        try
        {
            while (_isRunning && _cts is not null && !_cts.Token.IsCancellationRequested)
            {
                await ProcessOnceAsync(_cts.Token);
                await Task.Delay(Math.Clamp((int)Interval, 10, 1000), _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            OnLogMessage?.Invoke($"Processing error: {e.Message}");
        }
    }

    private async Task ProcessOnceAsync(CancellationToken ct)
    {
        lock (_syncLock)
        {
            var timestamp = (uint)Environment.TickCount;
            _kcp.Update(timestamp);
        }

        while (true)
        {
            int packetSize;
            byte[] buffer;
            int received;

            lock (_syncLock)
            {
                packetSize = _kcp.PeekSize();
                if (packetSize <= 0)
                    break;
                buffer = new byte[packetSize];
                received = _kcp.Recv(buffer);
            }

            if (0 < received)
            {
                if (_kcp.StreamMode == 0)
                    _receiveChannel?.Writer.TryWrite(buffer.AsMemory(0, received));
                else
                    await _stream.WriteReceivedDataAsync(buffer.AsMemory(0, received), ct);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(KcpTransport));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _stream.Dispose();

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        _receiveChannel?.Writer.Complete();
        _receiveChannel = null;

        try
        {
            _processingTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException) { }

        _kcp?.Dispose();
        _cts?.Dispose();
        _sendSemaphore?.Dispose();

        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        await _stream.DisposeAsync();

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        _receiveChannel?.Writer.Complete();
        _receiveChannel = null;

        if (_processingTask != null)
        {
            try
            {
                await Task.WhenAny(_processingTask, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            }
            catch { }
        }

        _kcp?.Dispose();
        _cts?.Dispose();
        _sendSemaphore?.Dispose();

        GC.SuppressFinalize(this);
    }
}
