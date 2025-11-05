namespace Kanawanagasaki.KCP.Tests;

using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.Interfaces;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public class KcpTransport_Tests
{
    [Fact]
    public async Task BasicCommunication()
    {
        var helloWorld = Encoding.UTF8.GetBytes("Hello, world!");

        var errors = new List<string>();

        using var client1 = new TestTransport(11111, 1.0);
        client1.OnLogMessage += msg => errors.Add(msg);
        using var client2 = new TestTransport(11111, 1.0);
        client2.OnLogMessage += msg => errors.Add(msg);

        client1.AnotherTransport = client2;
        client2.AnotherTransport = client1;

        client1.Start();
        client2.Start();

        client1.Write(helloWorld);

        var buffer = await client2.ReadAsync();

        Assert.Equal(helloWorld, buffer.ToArray());

        await client1.StopAsync();
        await client2.StopAsync();

        Assert.Empty(errors);
    }

    [Fact]
    public async Task MultipleMessagesSequential()
    {
        var messages = new byte[32][];
        for (int i = 0; i < messages.Length; i++)
            messages[i] = RandomNumberGenerator.GetBytes(Random.Shared.Next(128, 4096));

        var errors = new List<string>();
        var received = new List<byte[]>();

        using var client1 = new TestTransport(22222, 1.0);
        client1.SetWindowSize(256, 512);
        client1.SetNoDelay(true, 10, 2, true);
        client1.OnLogMessage += msg => errors.Add(msg);
        using var client2 = new TestTransport(22222, 1.0);
        client2.SetWindowSize(256, 512);
        client1.SetNoDelay(true, 10, 2, true);
        client2.OnLogMessage += msg => errors.Add(msg);

        client1.AnotherTransport = client2;
        client2.AnotherTransport = client1;

        client1.Start();
        client2.Start();

        foreach (var message in messages)
            client1.Write(message);

        for (int i = 0; i < messages.Length; i++)
        {
            var buffer = await client2.ReadAsync();
            received.Add(buffer.ToArray());
        }

        Assert.Equal(messages, received);

        await client1.StopAsync();
        await client2.StopAsync();

        Assert.Empty(errors);
    }

    [Fact]
    public async Task LargeDataTransfer()
    {
        var largeData = RandomNumberGenerator.GetBytes(20 * 1024);

        var errors = new List<string>();

        using var client1 = new TestTransport(33333, 1.0);
        client1.OnLogMessage += msg => errors.Add(msg);
        using var client2 = new TestTransport(33333, 1.0);
        client2.OnLogMessage += msg => errors.Add(msg);

        client1.AnotherTransport = client2;
        client2.AnotherTransport = client1;

        client1.Start();
        client2.Start();

        client1.Write(largeData);

        var received = await client2.ReadAsync();

        Assert.Equal(largeData, received.ToArray());

        await client1.StopAsync();
        await client2.StopAsync();

        Assert.Empty(errors);
    }

    [Fact]
    public async Task BidirectionalCommunication()
    {
        var message1 = Encoding.UTF8.GetBytes("Client 1 to Client 2");
        var message2 = Encoding.UTF8.GetBytes("Client 2 to Client 1");

        var errors = new List<string>();

        using var client1 = new TestTransport(44444, 1.0);
        client1.OnLogMessage += msg => errors.Add(msg);
        using var client2 = new TestTransport(44444, 1.0);
        client2.OnLogMessage += msg => errors.Add(msg);

        client1.AnotherTransport = client2;
        client2.AnotherTransport = client1;

        client1.Start();
        client2.Start();

        client1.Write(message1);
        client2.Write(message2);

        var received1 = await client2.ReadAsync();
        var received2 = await client1.ReadAsync();

        Assert.Equal(message1, received1.ToArray());
        Assert.Equal(message2, received2.ToArray());

        await client1.StopAsync();
        await client2.StopAsync();

        Assert.Empty(errors);
    }

    [Fact]
    public async Task ConcurrentOperations_LossyNetwork()
    {
        using var client1 = new TestTransport(55555, 0.8);
        client1.SetWindowSize(256, 256);
        client1.SetInterval(10);
        using var client2 = new TestTransport(55555, 0.8);
        client2.SetWindowSize(256, 256);
        client2.SetInterval(10);

        client1.AnotherTransport = client2;
        client2.AnotherTransport = client1;

        client1.Start();
        client2.Start();

        var tasks = new List<Task>();
        var sentCount = 256;

        for (int i = 0; i < sentCount; i++)
        {
            var _i = i;
            tasks.Add(Task.Run(() => client1.Write(BitConverter.GetBytes(_i))));
        }

        await Task.WhenAll(tasks);

        var received = new List<byte[]>();
        while (received.Count < sentCount)
        {
            try
            {
                var data = await client2.ReadAsync();
                if (!data.IsEmpty)
                    received.Add(data.ToArray());
            }
            catch (Exception)
            {
                break;
            }
        }

        Assert.Equal(sentCount, received.Count);

        for (int i = 0; i < sentCount; i++)
        {
            var msgBytes = BitConverter.GetBytes(i);
            Assert.Contains(msgBytes, received);
        }

        await client1.StopAsync();
        await client2.StopAsync();
    }

    [Fact]
    public async Task StreamInterface()
    {
        using var client1 = new TestTransport(66666, 1.0);
        using var client2 = new TestTransport(66666, 1.0);

        client1.SetStreamMode(true);
        client2.SetStreamMode(true);

        client1.AnotherTransport = client2;
        client2.AnotherTransport = client1;

        client1.Start();
        client2.Start();

        var testData = Encoding.UTF8.GetBytes("Stream test data");

        var stream = client1.GetStream();
        await stream.WriteAsync(testData);

        var receiveStream = client2.GetStream();
        var buffer = new byte[testData.Length];
        var bytesRead = await receiveStream.ReadAsync(buffer, 0, buffer.Length);

        Assert.True(0 < bytesRead);
        Assert.Equal(testData, buffer[..bytesRead]);

        await client1.StopAsync();
        await client2.StopAsync();
    }

    [Fact]
    public async Task LargeStreamingData()
    {
        using var client1 = new TestTransport(77777, 1.0);
        client1.SetStreamMode(true);
        client1.SetWindowSize(2048, 2048);
        client1.SetInterval(10);
        using var client2 = new TestTransport(77777, 1.0);
        client2.SetStreamMode(true);
        client2.SetWindowSize(2048, 2048);
        client2.SetInterval(10);

        client1.AnotherTransport = client2;
        client2.AnotherTransport = client1;

        client1.Start();
        client2.Start();

        var testData = new byte[32 * 1024 * 1024];
        Random.Shared.NextBytes(testData);

        var stream = client1.GetStream();
        var writeTask = stream.WriteAsync(testData);

        var receiveStream = client2.GetStream();
        var buffer = new byte[testData.Length];
        var receiveTask = receiveStream.ReadExactlyAsync(buffer);

        await writeTask;
        await receiveTask;

        Assert.Equal(testData, buffer);

        await client1.StopAsync();
        await client2.StopAsync();
    }

    public class TestTransport(uint conv, double _successChance) : KcpTransport(conv)
    {
        public TestTransport? AnotherTransport { get; set; }

        protected override async ValueTask<int> SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            if (_successChance <= Random.Shared.NextDouble())
                return 0;
            if (AnotherTransport is not null)
                return await AnotherTransport.InputAsync(data, ct).ConfigureAwait(false);
            return 0;
        }
    }

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

    internal async Task meow()
    {
        using var udpClient = new UdpClient();
        udpClient.Connect(IPAddress.Loopback, 34343);

        using var transport = new UdpTransport(udpClient, 1);
        transport.SetStreamMode(true);
        transport.Start();

        var cts = new CancellationTokenSource();
        var datagramTask = Task.Run(async () =>
        {
            try
            {
                var datagram = await udpClient.ReceiveAsync(cts.Token);
                await transport.InputAsync(datagram.Buffer, cts.Token);
            }
            catch (OperationCanceledException) { }
        });

        var stream = transport.GetStream();

        var helloWorld = Encoding.UTF8.GetBytes("Hello, world!");
        await stream.WriteAsync(helloWorld);

        var buffer = new byte[1024];
        var bytesRead = await stream.ReadAsync(buffer);
        var text = Encoding.UTF8.GetString(buffer);
        Console.WriteLine(text);

        cts.Cancel();
        await datagramTask;

        await transport.StopAsync();
        udpClient.Close();
    }
}
