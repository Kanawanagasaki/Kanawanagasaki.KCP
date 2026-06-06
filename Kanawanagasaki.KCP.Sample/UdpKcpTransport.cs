namespace Kanawanagasaki.KCP.Sample;

using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Kanawanagasaki.KCP;

public class UdpKcpTransport : KcpTransport
{
    private readonly Socket _socket;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _receiveLoop;
    private long _bytesSent;
    private long _bytesReceived;
    private long _packetsSent;
    private long _packetsReceived;

    private long _currentSecondNumber;
    private long _currentSecondSendBytes;
    private long _currentSecondRecvBytes;

    private long _lastSecondSendBytes;
    private long _lastSecondRecvBytes;
    private readonly Lock _secondLock = new();

    public IPEndPoint LocalEndpoint { get; }
    public IPEndPoint RemoteEndpoint { get; }

    public long UdpBytesSent => Volatile.Read(ref _bytesSent);
    public long UdpBytesReceived => Volatile.Read(ref _bytesReceived);
    public long UdpPacketsSent => Volatile.Read(ref _packetsSent);
    public long UdpPacketsReceived => Volatile.Read(ref _packetsReceived);

    public UdpKcpTransport(UdpClient udp, IPEndPoint localEndpoint, IPEndPoint remoteEndpoint, uint conversationId) : base(conversationId)
    {
        _socket = udp.Client;
        LocalEndpoint = localEndpoint;
        RemoteEndpoint = remoteEndpoint;
        _currentSecondNumber = Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        _receiveLoop = ReceiveLoop(_cts.Token);
    }

    private void RecordSendBytes(long count)
    {
        EnsureCurrentSecond();
        Interlocked.Add(ref _currentSecondSendBytes, count);
    }

    private void RecordReceiveBytes(long count)
    {
        EnsureCurrentSecond();
        Interlocked.Add(ref _currentSecondRecvBytes, count);
    }

    private void EnsureCurrentSecond()
    {
        var nowSecondNumber = Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        var storedSecondNumber = Volatile.Read(ref _currentSecondNumber);

        if (nowSecondNumber == storedSecondNumber)
            return;

        lock (_secondLock)
        {
            storedSecondNumber = _currentSecondNumber;
            if (nowSecondNumber == storedSecondNumber)
                return;

            if (nowSecondNumber == storedSecondNumber + 1)
            {
                _lastSecondSendBytes = _currentSecondSendBytes;
                _lastSecondRecvBytes = _currentSecondRecvBytes;
            }
            else
            {
                _lastSecondSendBytes = 0;
                _lastSecondRecvBytes = 0;
            }

            _currentSecondSendBytes = 0;
            _currentSecondRecvBytes = 0;
            _currentSecondNumber = nowSecondNumber;
        }
    }

    public long GetLastSecondSendBytes()
    {
        EnsureCurrentSecond();
        return Volatile.Read(ref _lastSecondSendBytes);
    }

    public long GetLastSecondReceiveBytes()
    {
        EnsureCurrentSecond();
        return Volatile.Read(ref _lastSecondRecvBytes);
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        try
        {
            var endpoint = (EndPoint)RemoteEndpoint;
            while (!ct.IsCancellationRequested)
            {
                var buffer = ArrayPool<byte>.Shared.Rent(65535);
                int received;
                try
                {
                    var result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, endpoint, ct);
                    received = result.ReceivedBytes;
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    throw;
                }

                if (0 < received)
                {
                    Interlocked.Add(ref _bytesReceived, received);
                    Interlocked.Increment(ref _packetsReceived);
                    RecordReceiveBytes(received);
                    Input(buffer.AsSpan(0, received).ToArray());
                }

                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    protected override async ValueTask<int> SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        try
        {
            var sent = await _socket.SendToAsync(data, SocketFlags.None, RemoteEndpoint, ct);
            Interlocked.Add(ref _bytesSent, sent);
            Interlocked.Increment(ref _packetsSent);
            RecordSendBytes(sent);
            return sent;
        }
        catch
        {
            return -1;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_cts.IsCancellationRequested)
        {
            _cts.Cancel();
            try
            {
                _receiveLoop.Wait(500);
            }
            catch { }
            _cts.Dispose();
        }
        _socket.Close();
        base.Dispose(disposing);
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
            try
            {
                await _receiveLoop;
            }
            catch { }
            _cts.Dispose();
        }
        _socket.Close();
        await base.DisposeAsyncCore();
    }
}
