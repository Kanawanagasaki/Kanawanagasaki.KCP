# KCP

[![NuGet package](https://img.shields.io/nuget/v/Kanawanagasaki.KCP.svg)](https://www.nuget.org/packages/Kanawanagasaki.KCP)

A transport-agnostic reliable protocol library for .NET. Ported from [https://github.com/skywind3000/kcp](https://github.com/skywind3000/kcp)

## What is KCP?

KCP is a fast, lightweight ARQ (Automatic Repeat reQuest) protocol that runs over unreliable transports like UDP.

## UDP example

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

### KcpConversation Interface

Direct protocol manipulation available through `KcpConversation`:

```csharp
using Kanawanagasaki.KCP;

using var kcp = new KcpConversation(34343);

kcp.SetOutput((data, kcpPtr, userPtr) =>
{
    // Implement transmission logic
    return data.Length;
});

// Configure protocol parameters
kcp.SetNoDelay(1, 10, 2, 1);
kcp.SetWndSize(32, 32);
kcp.SetMtu(1400);
kcp.SetStreamMode(false);

// Send operation
var message = Encoding.UTF8.GetBytes("Protocol data");
int sent = kcp.Send(message);

// Receive operation
var receiveBuffer = new byte[1024];
int received = kcp.Recv(receiveBuffer);

if (0 < received)
{
    var data = Encoding.UTF8.GetString(receiveBuffer[..received]);
}

// Protocol state maintenance
uint timestamp = (uint)Environment.TickCount;
kcp.Update(timestamp);

// Input processing
kcp.Input(incomingData);
```
