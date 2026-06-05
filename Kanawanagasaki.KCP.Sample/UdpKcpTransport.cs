namespace Kanawanagasaki.KCP.Sample;

using System.Net;
using System.Net.Sockets;
using Kanawanagasaki.KCP;

public class UdpKcpTransport : KcpTransport
{
    private readonly UdpClient _udp;
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
        _udp = udp;
        LocalEndpoint = localEndpoint;
        RemoteEndpoint = remoteEndpoint;
        _receiveLoop = ReceiveLoop(_cts.Token);
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await _udp.ReceiveAsync(ct);
                Interlocked.Add(ref _bytesReceived, result.Buffer.Length);
                Interlocked.Increment(ref _packetsReceived);
                Input(result.Buffer);
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
            var sent = await _udp.SendAsync(data, RemoteEndpoint, ct);
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
        _udp.Close();
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
        _udp.Close();
        await base.DisposeAsyncCore();
    }
}
