namespace Kanawanagasaki.KCP.Tests;

using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
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
        client1.SetInterval(10);
        client1.SetWindowSize(1024, 1024);
        using var client2 = new TestTransport(77777, 1.0);
        client2.SetStreamMode(true);
        client2.SetInterval(10);
        client2.SetWindowSize(1024, 1024);

        client1.AnotherTransport = client2;
        client2.AnotherTransport = client1;

        client1.Start();
        client2.Start();

        var testData = new byte[8 * 1024 * 1024];
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

    [Fact]
    public async Task LargeStreamingData_Bidirectional_Sequential()
    {
        using var client1 = new TestTransport(88888, 1.0);
        client1.SetStreamMode(true);
        client1.SetWindowSize(10240, 10240);
        client1.SetNoDelay(true, 10, 0, true);
        using var client2 = new TestTransport(88888, 1.0);
        client2.SetStreamMode(true);
        client2.SetWindowSize(10240, 10240);
        client2.SetNoDelay(true, 10, 0, true);

        client1.AnotherTransport = client2;
        client2.AnotherTransport = client1;

        client1.Start();
        client2.Start();

        var stream1 = client1.GetStream();
        var stream2 = client2.GetStream();

        for (int i = 0; i < 16; i++)
        {
            {
                var testData = new byte[16 * 1024 * 1024];
                Random.Shared.NextBytes(testData);

                var writeTask = stream1.WriteAsync(testData);

                var buffer = new byte[testData.Length];
                var receiveTask = stream2.ReadExactlyAsync(buffer);

                await writeTask;
                await receiveTask;

                Assert.Equal(testData, buffer);
            }

            {
                var testData = new byte[16 * 1024 * 1024];
                Random.Shared.NextBytes(testData);

                var writeTask = stream2.WriteAsync(testData);

                var buffer = new byte[testData.Length];
                var receiveTask = stream1.ReadExactlyAsync(buffer);

                await writeTask;
                await receiveTask;

                Assert.Equal(testData, buffer);
            }
        }

        await client1.StopAsync();
        await client2.StopAsync();
    }

    [Fact]
    public async Task LargeStreamingData_Bidirectional_Concurrent()
    {
        using var client1 = new TestTransport(88888, 1.0);
        client1.SetStreamMode(true);
        client1.SetWindowSize(10240, 10240);
        client1.SetNoDelay(true, 10, 0, true);
        using var client2 = new TestTransport(88888, 1.0);
        client2.SetStreamMode(true);
        client2.SetWindowSize(10240, 10240);
        client2.SetNoDelay(true, 10, 0, true);

        client1.AnotherTransport = client2;
        client2.AnotherTransport = client1;

        client1.Start();
        client2.Start();

        var stream1 = client1.GetStream();
        var stream2 = client2.GetStream();

        for (int i = 0; i < 16; i++)
        {
            var testData1 = new byte[16 * 1024 * 1024];
            Random.Shared.NextBytes(testData1);
            var writeTask1 = stream1.WriteAsync(testData1);

            var testData2 = new byte[16 * 1024 * 1024];
            Random.Shared.NextBytes(testData2);
            var writeTask2 = stream2.WriteAsync(testData2);

            var buffer1 = new byte[testData1.Length];
            var receiveTask1 = stream2.ReadExactlyAsync(buffer1);

            var buffer2 = new byte[testData2.Length];
            var receiveTask2 = stream1.ReadExactlyAsync(buffer2);

            await writeTask1;
            await writeTask2;
            await receiveTask1;
            await receiveTask2;

            Assert.Equal(testData1, buffer1);
            Assert.Equal(testData2, buffer2);
        }

        await client1.StopAsync();
        await client2.StopAsync();
    }

    [Fact]
    public async Task LargeStreamingDataSmallChunks()
    {
        using var client1 = new TestTransport(99999, 1.0);
        client1.SetStreamMode(true);
        client1.SetWindowSize(2048, 2048);
        client1.SetInterval(25);
        using var client2 = new TestTransport(99999, 1.0);
        client2.SetStreamMode(true);
        client2.SetWindowSize(2048, 2048);
        client2.SetInterval(25);

        client1.AnotherTransport = client2;
        client2.AnotherTransport = client1;

        client1.Start();
        client2.Start();

        var testData = new byte[32 * 1024 * 1024];
        Random.Shared.NextBytes(testData);

        var stream = client1.GetStream();
        var writeTask = Task.Run(async () =>
        {
            int offset = 0;
            while (offset < testData.Length)
            {
                var len = Math.Min(1024, testData.Length - offset);
                await stream.WriteAsync(testData.AsMemory(offset, len));
                offset += len;
            }
        });

        var receiveStream = client2.GetStream();
        var buffer = new byte[testData.Length];
        var receiveTask = receiveStream.ReadExactlyAsync(buffer);

        await writeTask;
        await receiveTask;

        Assert.Equal(testData, buffer);

        await client1.StopAsync();
        await client2.StopAsync();
    }

    [Fact]
    public async Task LargeStreamingData_LossyNetwork_NoCongestion()
    {
        using var client1 = new TestTransport(101010, 0.8);
        client1.SetStreamMode(true);
        client1.SetWindowSize(1024, 1024);
        client1.SetNoDelay(true, 10, 0, true);
        using var client2 = new TestTransport(101010, 0.8);
        client2.SetStreamMode(true);
        client2.SetWindowSize(1024, 1024);
        client2.SetNoDelay(true, 10, 0, true);

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

    [Fact]
    public async Task Start_MultipleTimes()
    {
        using var transport = new TestTransport(10001, 1.0);

        transport.Start();
        Assert.True(transport.IsRunning);

        transport.Start();
        Assert.True(transport.IsRunning);

        transport.Start();
        Assert.True(transport.IsRunning);

        await transport.StopAsync();
    }

    [Fact]
    public async Task StopAsync_MultipleTimes()
    {
        using var transport = new TestTransport(10002, 1.0);
        transport.Start();

        await transport.StopAsync();
        Assert.False(transport.IsRunning);

        await transport.StopAsync();
        Assert.False(transport.IsRunning);

        await transport.StopAsync();
        Assert.False(transport.IsRunning);
    }

    [Fact]
    public void Dispose_MultipleTimes()
    {
        var transport = new TestTransport(10003, 1.0);
        transport.Start();

        transport.Dispose();
        transport.Dispose();
        transport.Dispose();
    }

    [Fact]
    public async Task DisposeAsync_MultipleTimes()
    {
        var transport = new TestTransport(10004, 1.0);
        transport.Start();

        await transport.DisposeAsync();
        await transport.DisposeAsync();
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task Start_AfterStop()
    {
        using var transport = new TestTransport(10005, 1.0);

        transport.Start();
        Assert.True(transport.IsRunning);

        await transport.StopAsync();
        Assert.False(transport.IsRunning);

        transport.Start();
        Assert.True(transport.IsRunning);

        await transport.StopAsync();
    }

    [Fact]
    public async Task Start_MultipleTimes_AfterMultipleStops()
    {
        using var transport = new TestTransport(10006, 1.0);

        transport.Start();
        Assert.True(transport.IsRunning);
        await transport.StopAsync();
        Assert.False(transport.IsRunning);

        transport.Start();
        Assert.True(transport.IsRunning);
        await transport.StopAsync();
        Assert.False(transport.IsRunning);

        transport.Start();
        Assert.True(transport.IsRunning);
        await transport.StopAsync();
        Assert.False(transport.IsRunning);
    }

    [Fact]
    public async Task Stop_WithoutStart()
    {
        using var transport = new TestTransport(10007, 1.0);

        await transport.StopAsync();
        Assert.False(transport.IsRunning);

        await transport.StopAsync();
        await transport.StopAsync();
    }

    [Fact]
    public void Dispose_WithoutStart()
    {
        var transport = new TestTransport(10008, 1.0);
        transport.Dispose();
    }

    [Fact]
    public async Task DisposeAsync_WithoutStart()
    {
        var transport = new TestTransport(10009, 1.0);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_AfterStop()
    {
        var transport = new TestTransport(10010, 1.0);
        transport.Start();
        await transport.StopAsync();
        transport.Dispose();
    }

    [Fact]
    public async Task Stop_AfterDispose()
    {
        var transport = new TestTransport(10011, 1.0);
        transport.Start();
        transport.Dispose();
        await transport.StopAsync();
    }

    [Fact]
    public void Start_AfterDispose()
    {
        var transport = new TestTransport(10012, 1.0);
        transport.Start();
        transport.Dispose();

        Assert.Throws<ObjectDisposedException>(() => transport.Start());
    }

    [Fact]
    public async Task Start_AfterDisposeAsync()
    {
        var transport = new TestTransport(10013, 1.0);
        transport.Start();
        await transport.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => transport.Start());
    }

    [Fact]
    public async Task MixedStartStopDispose()
    {
        var transport = new TestTransport(10014, 1.0);

        transport.Start();
        Assert.True(transport.IsRunning);

        transport.Start();
        Assert.True(transport.IsRunning);

        await transport.StopAsync();
        Assert.False(transport.IsRunning);

        await transport.StopAsync();
        Assert.False(transport.IsRunning);

        transport.Start();
        Assert.True(transport.IsRunning);

        transport.Dispose();
        transport.Dispose();
    }

    [Fact]
    public async Task MixedStartStopDisposeAsync()
    {
        var transport = new TestTransport(10015, 1.0);

        transport.Start();
        Assert.True(transport.IsRunning);

        transport.Start();
        Assert.True(transport.IsRunning);

        await transport.StopAsync();
        Assert.False(transport.IsRunning);

        await transport.StopAsync();
        Assert.False(transport.IsRunning);

        transport.Start();
        Assert.True(transport.IsRunning);

        await transport.DisposeAsync();
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task Communication_AfterStopAndRestart()
    {
        var message1 = Encoding.UTF8.GetBytes("First message before stop");
        var message2 = Encoding.UTF8.GetBytes("Second message after restart");

        using var client1 = new TestTransport(20001, 1.0);
        using var client2 = new TestTransport(20001, 1.0);

        client1.AnotherTransport = client2;
        client2.AnotherTransport = client1;

        client1.Start();
        client2.Start();

        client1.Write(message1);
        var received1 = await client2.ReadAsync();
        Assert.Equal(message1, received1.ToArray());

        await client1.StopAsync();
        await client2.StopAsync();

        client1.Start();
        client2.Start();

        client1.Write(message2);
        var received2 = await client2.ReadAsync();
        Assert.Equal(message2, received2.ToArray());

        await client1.StopAsync();
        await client2.StopAsync();
    }

    [Fact]
    public async Task Communication_AfterMultipleStartStopCycles()
    {
        using var client1 = new TestTransport(20002, 1.0);
        using var client2 = new TestTransport(20002, 1.0);

        client1.AnotherTransport = client2;
        client2.AnotherTransport = client1;

        for (int cycle = 0; cycle < 5; cycle++)
        {
            var message = Encoding.UTF8.GetBytes($"Message cycle {cycle}");

            client1.Start();
            client2.Start();

            client1.Write(message);
            var received = await client2.ReadAsync();
            Assert.Equal(message, received.ToArray());

            await client1.StopAsync();
            await client2.StopAsync();
        }
    }

    [Fact]
    public async Task Communication_Bidirectional_AfterRestart()
    {
        using var client1 = new TestTransport(20003, 1.0);
        using var client2 = new TestTransport(20003, 1.0);

        client1.AnotherTransport = client2;
        client2.AnotherTransport = client1;

        client1.Start();
        client2.Start();

        var msg1to2 = Encoding.UTF8.GetBytes("Client1 to Client2 - first");
        var msg2to1 = Encoding.UTF8.GetBytes("Client2 to Client1 - first");

        client1.Write(msg1to2);
        client2.Write(msg2to1);

        var recv1 = await client2.ReadAsync();
        var recv2 = await client1.ReadAsync();

        Assert.Equal(msg1to2, recv1.ToArray());
        Assert.Equal(msg2to1, recv2.ToArray());

        await client1.StopAsync();
        await client2.StopAsync();

        client1.Start();
        client2.Start();

        var msg1to2_second = Encoding.UTF8.GetBytes("Client1 to Client2 - second");
        var msg2to1_second = Encoding.UTF8.GetBytes("Client2 to Client1 - second");

        client1.Write(msg1to2_second);
        client2.Write(msg2to1_second);

        var recv1_second = await client2.ReadAsync();
        var recv2_second = await client1.ReadAsync();

        Assert.Equal(msg1to2_second, recv1_second.ToArray());
        Assert.Equal(msg2to1_second, recv2_second.ToArray());

        await client1.StopAsync();
        await client2.StopAsync();
    }

    [Fact]
    public async Task Communication_LargeData_AfterRestart()
    {
        var largeData1 = RandomNumberGenerator.GetBytes(10 * 1024);
        var largeData2 = RandomNumberGenerator.GetBytes(15 * 1024);

        using var client1 = new TestTransport(20004, 1.0);
        using var client2 = new TestTransport(20004, 1.0);

        client1.AnotherTransport = client2;
        client2.AnotherTransport = client1;

        client1.Start();
        client2.Start();

        client1.Write(largeData1);
        var received1 = await client2.ReadAsync();
        Assert.Equal(largeData1, received1.ToArray());

        await client1.StopAsync();
        await client2.StopAsync();

        client1.Start();
        client2.Start();

        client1.Write(largeData2);
        var received2 = await client2.ReadAsync();
        Assert.Equal(largeData2, received2.ToArray());

        await client1.StopAsync();
        await client2.StopAsync();
    }

    [Fact]
    public async Task Communication_MultipleMessages_AfterRestart()
    {
        using var client1 = new TestTransport(20005, 1.0);
        using var client2 = new TestTransport(20005, 1.0);

        client1.SetWindowSize(256, 512);
        client2.SetWindowSize(256, 512);

        client1.AnotherTransport = client2;
        client2.AnotherTransport = client1;

        var messages1 = new byte[10][];
        for (int i = 0; i < messages1.Length; i++)
            messages1[i] = Encoding.UTF8.GetBytes($"First batch message {i}");

        client1.Start();
        client2.Start();

        foreach (var msg in messages1)
            client1.Write(msg);

        for (int i = 0; i < messages1.Length; i++)
        {
            var received = await client2.ReadAsync();
            Assert.Equal(messages1[i], received.ToArray());
        }

        await client1.StopAsync();
        await client2.StopAsync();

        var messages2 = new byte[10][];
        for (int i = 0; i < messages2.Length; i++)
            messages2[i] = Encoding.UTF8.GetBytes($"Second batch message {i}");

        client1.Start();
        client2.Start();

        foreach (var msg in messages2)
            client1.Write(msg);

        for (int i = 0; i < messages2.Length; i++)
        {
            var received = await client2.ReadAsync();
            Assert.Equal(messages2[i], received.ToArray());
        }

        await client1.StopAsync();
        await client2.StopAsync();
    }

    [Fact]
    public async Task Communication_WithLossyNetwork_AfterRestart()
    {
        using var client1 = new TestTransport(20006, 0.85);
        using var client2 = new TestTransport(20006, 0.85);

        client1.SetWindowSize(256, 256);
        client2.SetWindowSize(256, 256);
        client1.SetInterval(10);
        client2.SetInterval(10);

        client1.AnotherTransport = client2;
        client2.AnotherTransport = client1;

        client1.Start();
        client2.Start();

        var message1 = Encoding.UTF8.GetBytes("Lossy network test - first");
        client1.Write(message1);
        var received1 = await client2.ReadAsync();
        Assert.Equal(message1, received1.ToArray());

        await client1.StopAsync();
        await client2.StopAsync();

        client1.Start();
        client2.Start();

        var message2 = Encoding.UTF8.GetBytes("Lossy network test - second");
        client1.Write(message2);
        var received2 = await client2.ReadAsync();
        Assert.Equal(message2, received2.ToArray());

        await client1.StopAsync();
        await client2.StopAsync();
    }

    [Fact]
    public async Task StreamCommunication_AfterRestart()
    {
        using var client1 = new TestTransport(20007, 1.0);
        using var client2 = new TestTransport(20007, 1.0);

        client1.SetStreamMode(true);
        client2.SetStreamMode(true);

        client1.AnotherTransport = client2;
        client2.AnotherTransport = client1;

        client1.Start();
        client2.Start();

        var streamData1 = Encoding.UTF8.GetBytes("Stream data - first transfer");
        var stream1 = client1.GetStream();
        await stream1.WriteAsync(streamData1);

        var receiveStream1 = client2.GetStream();
        var buffer1 = new byte[streamData1.Length];
        await receiveStream1.ReadExactlyAsync(buffer1);
        Assert.Equal(streamData1, buffer1);

        await client1.StopAsync();
        await client2.StopAsync();

        client1.Start();
        client2.Start();

        var streamData2 = Encoding.UTF8.GetBytes("Stream data - second transfer after restart");
        var stream2 = client1.GetStream();
        await stream2.WriteAsync(streamData2);

        var receiveStream2 = client2.GetStream();
        var buffer2 = new byte[streamData2.Length];
        await receiveStream2.ReadExactlyAsync(buffer2);
        Assert.Equal(streamData2, buffer2);

        await client1.StopAsync();
        await client2.StopAsync();
    }

    [Fact]
    public async Task Communication_DoubleStart()
    {
        using var client1 = new TestTransport(20008, 1.0);
        using var client2 = new TestTransport(20008, 1.0);

        client1.AnotherTransport = client2;
        client2.AnotherTransport = client1;

        client1.Start();
        client1.Start();
        client2.Start();
        client2.Start();

        var message = Encoding.UTF8.GetBytes("Test after double start");
        client1.Write(message);

        var received = await client2.ReadAsync();
        Assert.Equal(message, received.ToArray());

        await client1.StopAsync();
        await client2.StopAsync();
    }

    [Fact]
    public async Task Communication_DoubleStop_ThenRestart()
    {
        using var client1 = new TestTransport(20009, 1.0);
        using var client2 = new TestTransport(20009, 1.0);

        client1.AnotherTransport = client2;
        client2.AnotherTransport = client1;

        client1.Start();
        client2.Start();

        var message1 = Encoding.UTF8.GetBytes("First message");
        client1.Write(message1);
        var received1 = await client2.ReadAsync();
        Assert.Equal(message1, received1.ToArray());

        await client1.StopAsync();
        await client1.StopAsync();
        await client2.StopAsync();
        await client2.StopAsync();

        client1.Start();
        client2.Start();

        var message2 = Encoding.UTF8.GetBytes("Second message after double stop");
        client1.Write(message2);
        var received2 = await client2.ReadAsync();
        Assert.Equal(message2, received2.ToArray());

        await client1.StopAsync();
        await client2.StopAsync();
    }

    [Fact]
    public async Task Communication_StartStopStart_WithDifferentDataSizes()
    {
        using var client1 = new TestTransport(20010, 0.75);
        using var client2 = new TestTransport(20010, 0.75);

        client1.AnotherTransport = client2;
        client2.AnotherTransport = client1;

        var dataSizes = new[] { 64, 512, 1024, 4096, 8192 };

        foreach (var size in dataSizes)
        {
            client1.Start();
            client2.Start();

            var data = RandomNumberGenerator.GetBytes(size);
            client1.Write(data);

            var received = await client2.ReadAsync();
            Assert.Equal(data, received.ToArray());

            await client1.StopAsync();
            await client2.StopAsync();
        }
    }

    public class TestTransport : KcpTransport
    {
        public TestTransport? AnotherTransport { get; set; }

        private readonly double _successChance;

        public Channel<byte[]> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();

        private readonly CancellationTokenSource _cts = new();
        private readonly Task _receiveTask;

        public TestTransport(uint conv, double successChance) : base(conv)
        {
            _successChance = successChance;
            _receiveTask = ReceiveLoopAsync(_cts.Token);
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var buffer = await Channel.Reader.ReadAsync(ct);
                    Input(buffer);
                }
            }
            catch (Exception) { }
        }

        protected override ValueTask<int> SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            if (_successChance <= Random.Shared.NextDouble())
                return ValueTask.FromResult(0);

            if (AnotherTransport is not null)
            {
                AnotherTransport.Channel.Writer.TryWrite(data.ToArray());
                return ValueTask.FromResult(data.Length);
            }

            return ValueTask.FromResult(0);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (!_cts.IsCancellationRequested)
                {
                    _cts.Cancel();
                    try
                    {
                        _receiveTask.Wait(TimeSpan.FromMilliseconds(500));
                    }
                    catch { }
                    _cts.Dispose();
                }
            }

            base.Dispose(disposing);
        }

        protected override async ValueTask DisposeAsyncCore()
        {
            if (!_cts.IsCancellationRequested)
            {
                _cts.Cancel();
                try
                {
                    await _receiveTask.ConfigureAwait(false);
                }
                catch { }
                _cts.Dispose();
            }

            await base.DisposeAsyncCore().ConfigureAwait(false);
        }
    }
}
