namespace Kanawanagasaki.KCP;

using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;

public abstract class KcpTransport : IAsyncDisposable, IDisposable
{
    private readonly KcpManaged _kcp;
    private readonly Lock _syncLock = new();

    private CancellationTokenSource? _cts;

    private Channel<PooledMemory>? _sendChannel;
    private Channel<ReadOnlyMemory<byte>>? _receiveChannel;

    private Task? _sendLoopTask;
    private Task? _updateLoopTask;
    private volatile int _disposeState = 0;
    private volatile bool _isRunning = false;

    private KcpConsumerProducerStream _stream;

    /// <summary>
    /// Raised when internal KCP events generate log messages.
    /// </summary>
    public event Action<string>? OnLogMessage;

    public uint ConversationId => _kcp.ConversationId;
    public bool NoDelay => _kcp.NoDelay;
    public uint Interval => _kcp.Interval;
    public uint SendWindow => _kcp.SendWindow;
    public uint ReceiveWindow => _kcp.ReceiveWindow;
    public uint RemoteWindow => _kcp.RemoteWindow;
    public uint Mtu => _kcp.Mtu;
    public uint MaximumSegmentSize => _kcp.MaximumSegmentSize;
    public bool IsStreamMode => _kcp.IsStreamMode;
    public uint State => _kcp.State;
    public bool IsDead => _kcp.IsDead;
    public uint SendUnacknowledged => _kcp.SendUnacknowledged;
    public uint SendNext => _kcp.SendNext;
    public uint ReceiveNext => _kcp.ReceiveNext;
    public uint SlowStartThreshold => _kcp.SlowStartThreshold;
    public int RoundTripTimeVariance => _kcp.RoundTripTimeVariance;
    public int SmoothedRoundTripTime => _kcp.SmoothedRoundTripTime;
    public int RetransmissionTimeout => _kcp.RetransmissionTimeout;
    public int MinimumRetransmissionTimeout => _kcp.MinimumRetransmissionTimeout;
    public uint CongestionWindow => _kcp.CongestionWindow;
    public uint CurrentTimestamp => _kcp.CurrentTimestamp;
    public uint NextFlushTimestamp => _kcp.NextFlushTimestamp;
    public uint RetransmissionCount => _kcp.RetransmissionCount;
    public uint ReceiveBufferCount => _kcp.ReceiveBufferCount;
    public uint SendBufferCount => _kcp.SendBufferCount;
    public uint ReceiveQueueCount => _kcp.ReceiveQueueCount;
    public uint SendQueueCount => _kcp.SendQueueCount;
    public bool IsUpdated => _kcp.IsUpdated;
    public uint DeadLink => _kcp.DeadLink;
    public uint CongestionWindowIncrement => _kcp.CongestionWindowIncrement;
    public int FastResend => _kcp.FastResend;
    public int FastLimit => _kcp.FastLimit;
    public bool NoCongestionWindow => _kcp.NoCongestionWindow;

    private uint _timestamp = 0;

    public bool IsDisposed => _disposeState != 0;

    /// <summary>
    /// Indicates if the transport is actively processing data.
    /// Returns false after disposal or explicit stop.
    /// </summary>
    public bool IsRunning => _isRunning && !IsDisposed;

    /// <summary>
    /// Initializes a new KCP transport session.
    /// </summary>
    /// <param name="conversationId">Unique session identifier</param>
    protected KcpTransport(uint conversationId)
    {
        _kcp = new KcpManaged(conversationId);
        _kcp.OnOutput = OnOutput;
        _kcp.OnLog = msg => OnLogMessage?.Invoke(msg);
        _stream = new KcpConsumerProducerStream(this);
    }

    private int OnOutput(ReadOnlySpan<byte> data)
    {
        if (IsDisposed || _cts?.IsCancellationRequested != false)
            return -1;

        var memoryOwner = MemoryPool<byte>.Shared.Rent(data.Length);
        var pooledMemory = new PooledMemory(memoryOwner, data.Length);
        data.CopyTo(pooledMemory.Memory.Span);

        _sendChannel?.Writer.TryWrite(pooledMemory);

        return data.Length;
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

            if ((int)_kcp.SendWindow <= _kcp.WaitSend())
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

        if (_kcp.IsStreamMode)
            throw new InvalidOperationException("In stream mode, obtain the stream using GetStream() and read data from it");

        if (_receiveChannel is null)
            return Memory<byte>.Empty;

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
    /// Calculates the amount of remaining free space in the send window, expressed in bytes,
    /// based on the current MTU and number of unacknowledged packets in flight.
    /// </summary>
    /// <returns>
    /// The number of bytes that can still be queued for sending without exceeding the send window,
    /// or 0 if the window is currently full.
    /// </returns>
    public int GetFreeSendWindowBytes()
    {
        var mss = Mtu - KcpConstants.IKCP_OVERHEAD;
        var freeSlots = (int)SendWindow - (int)GetWaitSnd();
        if (freeSlots <= 0)
            return 0;
        return freeSlots * (int)mss;
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

        _sendChannel?.Writer.Complete();
        _sendChannel = Channel.CreateUnbounded<PooledMemory>();

        _receiveChannel?.Writer.Complete();
        _receiveChannel = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

        _sendLoopTask = SendLoopAsync();
        _updateLoopTask = UpdateLoopAsync();
    }

    /// <summary>
    /// Stops processing.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (!_isRunning || IsDisposed)
            return;

        _isRunning = false;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        _sendChannel?.Writer?.Complete();
        _sendChannel = null;

        _receiveChannel?.Writer.Complete();
        _receiveChannel = null;

        if (_sendLoopTask is not null)
        {
            try
            {
                await _sendLoopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                OnLogMessage?.Invoke(e.Message);
            }
        }

        if (_updateLoopTask is not null)
        {
            try
            {
                await _updateLoopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                OnLogMessage?.Invoke(e.Message);
            }
        }
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
    public uint GetWaitSnd()
    {
        ThrowIfDisposed();
        return (uint)_kcp.WaitSend();
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
            _kcp.SetNoDelay(noDelay, intervalMs, fastResend, noCongestionControl);
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
            _kcp.SetWindowSize(sendWindow, receiveWindow);
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
            _kcp.IsStreamMode = enable;
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

    private async Task SendLoopAsync()
    {
        try
        {
            if (_cts is null)
                return;

            var ct = _cts.Token;
            while (_isRunning && !ct.IsCancellationRequested && _sendChannel is not null)
            {
                var pooledMemory = await _sendChannel.Reader.ReadAsync(ct);
                await SendAsync(pooledMemory.Memory, ct);
                pooledMemory.Dispose();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            OnLogMessage?.Invoke($"Processing error: {e.Message}");
        }
    }

    private async Task UpdateLoopAsync()
    {
        try
        {
            if (_cts is null)
                return;

            var ct = _cts.Token;
            var startTime = Stopwatch.GetTimestamp();
            var startTimestamp = _timestamp;
            while (_isRunning && !ct.IsCancellationRequested)
            {
                var interval = Interval;
                var expectedTimestamp = startTimestamp + Stopwatch.GetElapsedTime(startTime).TotalMilliseconds;

                int processCount = 0;
                while (_timestamp < expectedTimestamp && processCount < 16)
                {
                    await UpdateOnceAsync(_timestamp, ct);
                    _timestamp += interval;
                    processCount++;
                }

                if (16 <= processCount)
                    _timestamp = (uint)expectedTimestamp;

                await Task.Delay(Math.Clamp((int)interval, 10, 1000), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            OnLogMessage?.Invoke($"Processing error: {e.Message}");
        }
    }

    private async Task UpdateOnceAsync(uint timestamp, CancellationToken ct)
    {
        lock (_syncLock)
            _kcp.Update(timestamp);

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
                received = _kcp.Receive(buffer);
            }

            if (0 < received)
            {
                if (_kcp.IsStreamMode)
                    await _stream.WriteReceivedDataAsync(buffer.AsMemory(0, received), ct);
                else
                    _receiveChannel?.Writer.TryWrite(buffer.AsMemory(0, received));
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (IsDisposed)
            throw new ObjectDisposedException(nameof(KcpTransport));
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore().ConfigureAwait(false);
        Dispose(disposing: false);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        _isRunning = false;

        if (disposing)
        {
            if (_cts is not null && !_cts.IsCancellationRequested)
            {
                try
                {
                    _cts.Cancel();
                }
                catch (ObjectDisposedException) { }
            }

            try
            {
                _sendLoopTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch { }
            try
            {
                _updateLoopTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch { }

            _stream?.Dispose();

            _sendChannel?.Writer.TryComplete();
            _receiveChannel?.Writer.TryComplete();

            lock (_syncLock)
                _kcp?.Dispose();

            _cts?.Dispose();
        }
    }

    protected virtual async ValueTask DisposeAsyncCore()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        _isRunning = false;

        if (_cts is not null && !_cts.IsCancellationRequested)
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException) { }
        }

        var tasks = new List<Task>(2);
        if (_sendLoopTask is not null)
            tasks.Add(_sendLoopTask);
        if (_updateLoopTask is not null)
            tasks.Add(_updateLoopTask);

        if (0 < tasks.Count)
        {
            try
            {
                await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            }
            catch { }
        }

        if (_stream is IAsyncDisposable asyncStream)
        {
            await asyncStream.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            _stream?.Dispose();
        }

        _sendChannel?.Writer.TryComplete();
        _sendChannel = null;

        _receiveChannel?.Writer.TryComplete();
        _receiveChannel = null;

        lock (_syncLock)
            _kcp?.Dispose();

        _cts?.Dispose();
        _cts = null;
    }
}
