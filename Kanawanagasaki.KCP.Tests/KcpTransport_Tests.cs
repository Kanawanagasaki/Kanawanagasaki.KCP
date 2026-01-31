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
            _receiveTask = ReceiveAsync();
        }

        private async Task ReceiveAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var buffer = await Channel.Reader.ReadAsync(_cts.Token);
                    Input(buffer);
                }
                catch { }
            }
        }

        protected override async ValueTask<int> SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            if (_successChance <= Random.Shared.NextDouble())
                return data.Length;

            if (AnotherTransport is not null)
            {
                await AnotherTransport.Channel.Writer.WriteAsync(data.ToArray(), ct);
                return data.Length;
            }

            return 0;
        }

        public override void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
            base.Dispose();
        }

        public override async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _cts.Dispose();
            await _receiveTask;
            await base.DisposeAsync();
        }
    }
}
