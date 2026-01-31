# KCP

[![NuGet package](https://img.shields.io/nuget/v/Kanawanagasaki.KCP.svg)](https://www.nuget.org/packages/Kanawanagasaki.KCP)

A transport-agnostic reliable protocol library for .NET. Ported from [https://github.com/skywind3000/kcp](https://github.com/skywind3000/kcp)

## What is KCP?

KCP is a fast, lightweight ARQ (Automatic Repeat reQuest) protocol that runs over unreliable transports like UDP.

## KcpTransport

For most applications, use the `KcpTransport` abstraction:

```csharp
using Kanawanagasaki.KCP;

class UdpTransport : KcpTransport
{
    private readonly UdpClient _udpClient;

    public UdpTransport(UdpClient udpClient, uint conversationId) : base(conversationId)
    {
        _udpClient = udpClient;
    }

    protected override async ValueTask<int> SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        return await _udpClient.SendAsync(data, ct);
    }
}

using var udpClient = new UdpClient();
udpClient.Connect(IPAddress.Loopback, 34343);

using var transport = new UdpTransport(udpClient, 1);
transport.Start();

var payload = Encoding.UTF8.GetBytes("Hello, world!");
transport.Write(payload);

// The UDP transport delivers a datagram containing a KCP-formatted payload
var datagram = await udpClient.ReceiveAsync();
// This payload must be fed into the KCP transport's input buffer before the application can read the decoded user data
transport.Input(datagram.Buffer);

var buffer = await transport.ReadAsync();
var response = Encoding.UTF8.GetString(buffer.Span);
Console.WriteLine(response);

await transport.StopAsync();
udpClient.Close();
```

## KcpManaged

`KcpManaged` provides a managed, disposable wrapper around the native KCP protocol implementation. It offers a middle-ground API between the high-level KcpTransport and the raw unsafe pointers.

```csharp
using Kanawanagasaki.KCP;

using var kcp = new KcpManaged(12345);

// Configure output callback - called when KCP has packets ready to send
kcp.OnOutput = (data) =>
{
    udpClient.Send(data);
    return data.Length;
};

kcp.SetNoDelay(nodelay: true, interval: 10, resend: 2, noCongestionWindow: false);
kcp.SetWindowSize(sndwnd: 128, rcvwnd: 128);
kcp.SetMtu(1400);

var message = Encoding.UTF8.GetBytes("Hello, World!");
int sent = kcp.Send(message);

kcp.Input(receivedPacketSpan);
kcp.Update((uint)Environment.TickCount);

var buffer = new byte[4096];
int received = kcp.Receive(buffer);
if (received > 0)
{
    var data = buffer[..received];
}
```

## Configuration Settings

### SetNoDelay

Configures delay acknowledgment and congestion control mechanisms

```csharp
transport.SetNoDelay(noDelay: false, intervalMs: 100, fastResend: 0, noCongestionControl: false);
```

**Parameters:**

- `noDelay`: Disables delayed acknowledgments when true, ACKs are transmitted immediately, reducing RTT at the cost of increased packet count.
- `intervalMs`: Timer granularity for acknowledgment processing. Lower values reduce latency but increase CPU overhead.
- `fastResend`: Number of duplicate acknowledgments required to trigger fast retransmission. Set to 0 to disable fast retransmission.
- `noCongestionControl`: Disables congestion control mechanisms when true. When false, gradually adjust send window size.


### SetWindowSize

Controls send and receive buffer sizes

```csharp
transport.SetWindowSize(sendWindow: 32, receiveWindow: 32);
```

Buffer sizes determine the number of unacknowledged packets maintained in memory. Operations exceeding the configured window size result in exceptions. Segments exceeding MSS (Maximum Segment Size) are automatically fragmented and will fill send window faster.

**Defaults:** sendWindow = 32, receiveWindow = 32

### SetMtu - Packet Size

Configures Maximum Transmission Unit for packet fragmentation

```csharp
transport.SetMtu(mtu: 1400);
```

Default: 1400

## Stream Interface

The library provides two delivery modes:

### Normal Mode (Default)

Maintains message boundaries. Each write operation corresponds to exactly one read operation.

```csharp
transport.SetStreamMode(false); // Default
```

### Stream Mode

Treats data as continuous byte stream without boundary preservation:

```csharp
transport.SetStreamMode(true);
var stream = transport.GetStream();
await stream.WriteAsync(data);
int bytesRead = await stream.ReadAsync(buffer);
```

Stream mode is recommended for continuous data transfer operations where message boundaries are not semantically significant.


## Low-Level API

### KCP Interface

This API uses unmanaged memory and requires unsafe context. You must manually ensure thread safety and proper memory cleanup to avoid leaks or corruption.

```csharp
using Kanawanagasaki.KCP;
using System.Runtime.InteropServices;

// Create KCP control block (IKCPCB)
uint conversationId = 12345;
IKCPCB* kcp = KCP.ikcp_create(conversationId, userData: null);

if (kcp == null)
    throw new OutOfMemoryException("Failed to create KCP instance");

try
{
    // Set output callback using unmanaged function pointer
    KCP.ikcp_setoutput(kcp, &MyOutputCallback);
    
    // Optional: Set logging callback
    kcp->writelog = &MyLogCallback;
    kcp->logmask = KcpConstants.IKCP_LOG_OUTPUT | KcpConstants.IKCP_LOG_INPUT;

    // Configure protocol
    KCP.ikcp_nodelay(kcp, nodelay: 1, interval: 10, resend: 2, nc: 0);
    KCP.ikcp_wndsize(kcp, sndwnd: 128, rcvwnd: 128);
    KCP.ikcp_setmtu(kcp, mtu: 1400);

    // Send data
    byte[] message = Encoding.UTF8.GetBytes("Protocol data");
    fixed (byte* ptr = message)
    {
        int sent = KCP.ikcp_send(kcp, ptr, message.Length);
        if (sent < 0) throw new InvalidOperationException($"Send failed: {sent}");
    }

    // Main loop - call Update regularly
    while (true)
    {
        uint current = (uint)Environment.TickCount;
        KCP.ikcp_update(kcp, current);
        
        // Check when next update is needed (for sleep/timer optimization)
        uint nextUpdate = KCP.ikcp_check(kcp, current);
        int sleepMs = (int)(nextUpdate - current);
        if (sleepMs > 0)
            Thread.Sleep(Math.Min(sleepMs, 100));
        
        // Process incoming packets from network
        if (packetReceived)
        {
            fixed (byte* packetPtr = receivedPacket)
            {
                int result = KCP.ikcp_input(kcp, packetPtr, receivedPacket.Length);
                if (result < 0) 
                    Console.WriteLine($"Input error: {result}");
            }
        }
        
        // Receive decoded data
        byte* recvBuffer = stackalloc byte[4096];
        int received = KCP.ikcp_recv(kcp, recvBuffer, 4096);
        if (received > 0)
        {
            // Process received data
            ProcessData(recvBuffer, received);
        }
    }
}
finally
{
    // Release native memory
    KCP.ikcp_release(kcp);
}

[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
private static int MyOutputCallback(byte* data, int size, IKCPCB* kcp, void* user)
{
    var span = new ReadOnlySpan<byte>(data, size);
    UdpSend(span);
    return size;
}

[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
private static void MyLogCallback(byte* log, IKCPCB* kcp, void* user)
{
    string message = Marshal.PtrToStringUTF8((IntPtr)log);
    Console.WriteLine($"[KCP] {message}");
}
```
