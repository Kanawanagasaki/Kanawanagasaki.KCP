namespace Kanawanagasaki.KCP.Sample;

using System.Buffers;
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
        _receiveLoop = ReceiveLoop(_cts.Token);
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
