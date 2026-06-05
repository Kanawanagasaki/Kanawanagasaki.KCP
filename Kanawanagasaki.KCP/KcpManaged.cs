namespace Kanawanagasaki.KCP;

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

public unsafe class KcpManaged : IDisposable
{
    private IKCPCB* _kcp;
    private GCHandle _gcHandle;
    private bool _disposed;

    private static int _poolInstalled;

    public delegate int OutputCallback(ReadOnlySpan<byte> data);

    /// <summary>
    /// A callback invoked when KCP has data ready to send through the transport layer.
    /// </summary>
    public OutputCallback? OnOutput { get; set; }

    public delegate void LogCallback(string message);

    /// <summary>
    /// A callback invoked when KCP generates internal log messages.
    /// </summary>
    public LogCallback? OnLog { get; set; }

    /// <summary>
    /// Initializes a new instance of the KCP protocol with the specified conversation ID.
    /// </summary>
    /// <param name="conversationId">Unique session identifier for this KCP connection.</param>
    /// <exception cref="InvalidOperationException">Thrown when native KCP instance creation fails.</exception>
    public KcpManaged(uint conversationId)
    {
        EnsurePoolInstalled();

        _gcHandle = GCHandle.Alloc(this);
        _kcp = KCP.ikcp_create(conversationId, (void*)GCHandle.ToIntPtr(_gcHandle));

        if (_kcp == null)
        {
            _gcHandle.Free();
            throw new InvalidOperationException("Failed to create KCP instance");
        }

        KCP.ikcp_setoutput(_kcp, &OutputUnmanaged);
        _kcp->writelog = &LogUnmanaged;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OutputUnmanaged(byte* data, int size, IKCPCB* kcp, void* user)
    {
        var handle = GCHandle.FromIntPtr((IntPtr)user);
        if (!handle.IsAllocated)
            return -1;

        var conversation = handle.Target as KcpManaged;
        if (conversation?._disposed != false)
            return -1;

        var callback = conversation.OnOutput;
        if (callback == null)
            return 0;

        return callback(new ReadOnlySpan<byte>(data, size));
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void LogUnmanaged(byte* log, IKCPCB* kcp, void* user)
    {
        var handle = GCHandle.FromIntPtr((IntPtr)user);
        if (!handle.IsAllocated)
            return;

        var conversation = handle.Target as KcpManaged;
        if (conversation?._disposed != false)
            return;

        var callback = conversation.OnLog;
        if (callback == null)
            return;

        var message = Marshal.PtrToStringUTF8((IntPtr)log);
        if (message is not null)
            callback(message);
    }

    /// <summary>
    /// Gets the unique conversation ID for this KCP session.
    /// </summary>
    public uint ConversationId
    {
        get { ThrowIfDisposed(); return _kcp->conv; }
    }

    /// <summary>
    /// Gets or sets a value indicating whether nodelay mode is enabled.
    /// When true, ACKs are sent immediately reducing latency but increasing packet count.
    /// When false, ACKs are delayed to combine multiple acknowledgments.
    /// </summary>
    public bool NoDelay
    {
        get { ThrowIfDisposed(); return _kcp->nodelay != 0; }
        set { ThrowIfDisposed(); _kcp->nodelay = value ? 1u : 0u; }
    }

    /// <summary>
    /// Gets or sets the internal protocol update interval in milliseconds.
    /// Lower values reduce latency but increase CPU usage. Valid range is 10-5000ms.
    /// </summary>
    public uint Interval
    {
        get { ThrowIfDisposed(); return _kcp->interval; }
        set { ThrowIfDisposed(); _kcp->interval = value; }
    }

    /// <summary>
    /// Gets or sets the maximum number of unacknowledged packets allowed (send window size).
    /// </summary>
    public uint SendWindow
    {
        get { ThrowIfDisposed(); return _kcp->snd_wnd; }
        set { ThrowIfDisposed(); _kcp->snd_wnd = value; }
    }

    /// <summary>
    /// Gets or sets the maximum number of out-of-order packets to buffer (receive window size).
    /// </summary>
    public uint ReceiveWindow
    {
        get { ThrowIfDisposed(); return _kcp->rcv_wnd; }
        set { ThrowIfDisposed(); _kcp->rcv_wnd = value; }
    }

    /// <summary>
    /// Gets the remote window size advertised by the peer.
    /// </summary>
    public uint RemoteWindow
    {
        get { ThrowIfDisposed(); return _kcp->rmt_wnd; }
    }

    /// <summary>
    /// Gets the Maximum Transmission Unit (MTU) size in bytes.
    /// </summary>
    public uint Mtu
    {
        get { ThrowIfDisposed(); return _kcp->mtu; }
    }

    /// <summary>
    /// Gets the Maximum Segment Size (MSS) which is MTU minus protocol overhead.
    /// </summary>
    public uint MaximumSegmentSize
    {
        get { ThrowIfDisposed(); return _kcp->mss; }
    }

    /// <summary>
    /// Gets or sets a value indicating whether stream mode is enabled.
    /// In stream mode, data is treated as a continuous byte stream without message boundaries.
    /// In message mode (default), message boundaries are preserved.
    /// </summary>
    public bool IsStreamMode
    {
        get { ThrowIfDisposed(); return _kcp->stream != 0; }
        set { ThrowIfDisposed(); _kcp->stream = value ? 1 : 0; }
    }

    /// <summary>
    /// Gets the current connection state.
    /// Returns -1 (unchecked uint) when the connection is dead.
    /// </summary>
    public uint State
    {
        get { ThrowIfDisposed(); return _kcp->state; }
    }

    /// <summary>
    /// Gets a value indicating whether the connection is dead (broken).
    /// </summary>
    public bool IsDead => State == unchecked((uint)-1);

    /// <summary>
    /// Gets the sequence number of the first unacknowledged packet (send_una).
    /// </summary>
    public uint SendUnacknowledged
    {
        get { ThrowIfDisposed(); return _kcp->snd_una; }
    }

    /// <summary>
    /// Gets the next sequence number to be sent (send_nxt).
    /// </summary>
    public uint SendNext
    {
        get { ThrowIfDisposed(); return _kcp->snd_nxt; }
    }

    /// <summary>
    /// Gets the next sequence number expected to be received (rcv_nxt).
    /// </summary>
    public uint ReceiveNext
    {
        get { ThrowIfDisposed(); return _kcp->rcv_nxt; }
    }

    /// <summary>
    /// Gets the slow start threshold for congestion control (ssthresh).
    /// </summary>
    public uint SlowStartThreshold
    {
        get { ThrowIfDisposed(); return _kcp->ssthresh; }
    }

    /// <summary>
    /// Gets the round-trip time variance (rx_rttval) for congestion control calculations.
    /// </summary>
    public int RoundTripTimeVariance
    {
        get { ThrowIfDisposed(); return _kcp->rx_rttval; }
    }

    /// <summary>
    /// Gets the smoothed round-trip time (rx_srtt) in milliseconds.
    /// </summary>
    public int SmoothedRoundTripTime
    {
        get { ThrowIfDisposed(); return _kcp->rx_srtt; }
    }

    /// <summary>
    /// Gets the current retransmission timeout (rx_rto) in milliseconds.
    /// </summary>
    public int RetransmissionTimeout
    {
        get { ThrowIfDisposed(); return _kcp->rx_rto; }
    }

    /// <summary>
    /// Gets or sets the minimum retransmission timeout (rx_minrto) in milliseconds.
    /// </summary>
    public int MinimumRetransmissionTimeout
    {
        get { ThrowIfDisposed(); return _kcp->rx_minrto; }
        set { ThrowIfDisposed(); _kcp->rx_minrto = value; }
    }

    /// <summary>
    /// Gets the current congestion window size (cwnd).
    /// </summary>
    public uint CongestionWindow
    {
        get { ThrowIfDisposed(); return _kcp->cwnd; }
    }

    /// <summary>
    /// Gets the current timestamp used by the protocol (current).
    /// </summary>
    public uint CurrentTimestamp
    {
        get { ThrowIfDisposed(); return _kcp->current; }
    }

    /// <summary>
    /// Gets the timestamp when the next flush should occur (ts_flush).
    /// </summary>
    public uint NextFlushTimestamp
    {
        get { ThrowIfDisposed(); return _kcp->ts_flush; }
    }

    /// <summary>
    /// Gets the total number of packet transmissions including retransmissions (xmit).
    /// </summary>
    public uint RetransmissionCount
    {
        get { ThrowIfDisposed(); return _kcp->xmit; }
    }

    /// <summary>
    /// Gets the number of segments currently in the receive buffer (nrcv_buf).
    /// These are out-of-order packets waiting for earlier packets to arrive.
    /// </summary>
    public uint ReceiveBufferCount
    {
        get { ThrowIfDisposed(); return _kcp->nrcv_buf; }
    }

    /// <summary>
    /// Gets the number of segments currently in the send buffer (nsnd_buf).
    /// These are packets sent but not yet acknowledged.
    /// </summary>
    public uint SendBufferCount
    {
        get { ThrowIfDisposed(); return _kcp->nsnd_buf; }
    }

    /// <summary>
    /// Gets the number of segments ready to be received by the application (nrcv_que).
    /// </summary>
    public uint ReceiveQueueCount
    {
        get { ThrowIfDisposed(); return _kcp->nrcv_que; }
    }

    /// <summary>
    /// Gets the number of segments queued for sending (nsnd_que).
    /// </summary>
    public uint SendQueueCount
    {
        get { ThrowIfDisposed(); return _kcp->nsnd_que; }
    }

    /// <summary>
    /// Gets a value indicating whether the protocol has been updated at least once.
    /// </summary>
    public bool IsUpdated
    {
        get { ThrowIfDisposed(); return _kcp->updated != 0; }
    }

    /// <summary>
    /// Gets the dead link threshold. When retransmissions exceed this value, the connection is marked as dead.
    /// </summary>
    public uint DeadLink
    {
        get { ThrowIfDisposed(); return _kcp->dead_link; }
    }

    /// <summary>
    /// Gets the congestion window increment value (incr).
    /// </summary>
    public uint CongestionWindowIncrement
    {
        get { ThrowIfDisposed(); return _kcp->incr; }
    }

    /// <summary>
    /// Gets or sets the fast resend threshold. When set to N (N>0), KCP will resend a packet after receiving N duplicate ACKs.
    /// Set to 0 to disable fast retransmit.
    /// </summary>
    public int FastResend
    {
        get { ThrowIfDisposed(); return _kcp->fastresend; }
        set { ThrowIfDisposed(); _kcp->fastresend = value; }
    }

    /// <summary>
    /// Gets the maximum number of times a packet can be fast-resent (fastlimit).
    /// </summary>
    public int FastLimit
    {
        get { ThrowIfDisposed(); return _kcp->fastlimit; }
    }

    /// <summary>
    /// Gets or sets a value indicating whether congestion control is disabled (nocwnd).
    /// When true, packets are sent aggressively without congestion window restrictions.
    /// </summary>
    public bool NoCongestionWindow
    {
        get { ThrowIfDisposed(); return _kcp->nocwnd != 0; }
        set { ThrowIfDisposed(); _kcp->nocwnd = value ? 1 : 0; }
    }

    /// <summary>
    /// Gets or sets the log mask for filtering protocol log messages.
    /// </summary>
    public int LogMask
    {
        get { ThrowIfDisposed(); return _kcp->logmask; }
        set { ThrowIfDisposed(); _kcp->logmask = value; }
    }

    /// <summary>
    /// Queues application data for reliable transmission through KCP.
    /// Data is fragmented, sequenced, and will be retransmitted as needed.
    /// </summary>
    /// <param name="data">Application payload to send.</param>
    /// <returns>Number of bytes queued, or negative value on error.</returns>
    public int Send(ReadOnlySpan<byte> data)
    {
        ThrowIfDisposed();
        if (data.IsEmpty)
            return 0;

        fixed (byte* ptr = data)
        {
            return KCP.ikcp_send(_kcp, ptr, data.Length);
        }
    }

    /// <summary>
    /// Receives the next reconstructed application packet from the receive queue.
    /// </summary>
    /// <param name="buffer">Buffer to receive the data into.</param>
    /// <returns>Number of bytes received, or negative value if no data available.</returns>
    public int Receive(Span<byte> buffer)
    {
        ThrowIfDisposed();
        if (buffer.IsEmpty)
            return 0;

        fixed (byte* ptr = buffer)
        {
            return KCP.ikcp_recv(_kcp, ptr, buffer.Length);
        }
    }

    /// <summary>
    /// Peeks at the size of the next available packet without removing it from the queue.
    /// </summary>
    /// <returns>Size of the next packet in bytes, or negative value if no packet available.</returns>
    public int PeekSize()
    {
        ThrowIfDisposed();
        return KCP.ikcp_peeksize(_kcp);
    }

    /// <summary>
    /// Inputs raw KCP protocol data received from the network.
    /// This should be called when KCP-formatted packets are received from the transport layer.
    /// </summary>
    /// <param name="data">Raw KCP packet data received from the network.</param>
    /// <returns>0 on success, negative value on error.</returns>
    public int Input(ReadOnlySpan<byte> data)
    {
        ThrowIfDisposed();
        if (data.IsEmpty) return 0;

        fixed (byte* ptr = data)
        {
            return KCP.ikcp_input(_kcp, ptr, data.Length);
        }
    }

    /// <summary>
    /// Updates the KCP protocol state machine with the current timestamp.
    /// This should be called regularly to handle retransmissions, congestion control, and flushing.
    /// </summary>
    /// <param name="currentTimestamp">Current timestamp in milliseconds.</param>
    public void Update(uint currentTimestamp)
    {
        ThrowIfDisposed();
        KCP.ikcp_update(_kcp, currentTimestamp);
    }

    /// <summary>
    /// Checks when the next update should be called.
    /// </summary>
    /// <param name="currentTimestamp">Current timestamp in milliseconds.</param>
    /// <returns>The timestamp when Update should be called next.</returns>
    public uint Check(uint currentTimestamp)
    {
        ThrowIfDisposed();
        return KCP.ikcp_check(_kcp, currentTimestamp);
    }

    /// <summary>
    /// Immediately flushes all pending packets (ACKs, data packets, window probes).
    /// </summary>
    public void Flush()
    {
        ThrowIfDisposed();
        KCP.ikcp_flush(_kcp);
    }

    /// <summary>
    /// Sets the Maximum Transmission Unit (MTU) size.
    /// </summary>
    /// <param name="mtu">New MTU size in bytes. Must be at least 50 and greater than IKCP_OVERHEAD.</param>
    /// <returns>0 on success, -1 if MTU is too small, -2 if memory allocation failed.</returns>
    public int SetMtu(int mtu)
    {
        ThrowIfDisposed();
        return KCP.ikcp_setmtu(_kcp, mtu);
    }

    /// <summary>
    /// Sets the internal update interval.
    /// </summary>
    /// <param name="interval">Interval in milliseconds (10-5000).</param>
    /// <returns>0 on success.</returns>
    public int SetInterval(int interval)
    {
        ThrowIfDisposed();
        return KCP.ikcp_interval(_kcp, interval);
    }

    /// <summary>
    /// Configures KCP timing and reliability parameters for different use cases.
    /// </summary>
    /// <param name="nodelay">Disable ACK delay for lower latency when true.</param>
    /// <param name="interval">Internal timer resolution in milliseconds (-1 to keep current).</param>
    /// <param name="resend">Fast retransmission threshold (-1 to keep current, 0 to disable, 2 recommended for real-time).</param>
    /// <param name="noCongestionWindow">When true, disables congestion control for aggressive sending.</param>
    /// <returns>0 on success.</returns>
    public int SetNoDelay(bool nodelay, int interval = -1, int resend = -1, bool noCongestionWindow = false)
    {
        ThrowIfDisposed();
        return KCP.ikcp_nodelay(_kcp, nodelay ? 1 : 0, interval, resend, noCongestionWindow ? 1 : 0);
    }

    /// <summary>
    /// Sets the send and receive window sizes for flow control.
    /// </summary>
    /// <param name="sendWindow">Send window size in packets.</param>
    /// <param name="receiveWindow">Receive window size in packets.</param>
    /// <returns>0 on success.</returns>
    public int SetWindowSize(int sendWindow, int receiveWindow)
    {
        ThrowIfDisposed();
        return KCP.ikcp_wndsize(_kcp, sendWindow, receiveWindow);
    }

    /// <summary>
    /// Gets the number of packets waiting to be sent (in send queue and buffer).
    /// </summary>
    /// <returns>Number of pending packets.</returns>
    public int WaitSend()
    {
        ThrowIfDisposed();
        return KCP.ikcp_waitsnd(_kcp);
    }

    private static void EnsurePoolInstalled()
    {
        if (Interlocked.CompareExchange(ref _poolInstalled, 1, 0) == 0)
        {
            KcpMemoryPool.Install();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(KcpManaged));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_kcp != null)
        {
            KCP.ikcp_release(_kcp);
            _kcp = null;
        }

        if (_gcHandle.IsAllocated)
        {
            _gcHandle.Free();
        }

        GC.SuppressFinalize(this);
    }

    ~KcpManaged()
    {
        Dispose();
    }
}
