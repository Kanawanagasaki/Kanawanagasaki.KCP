namespace Kanawanagasaki.KCP.Tests;

using System.Security.Cryptography;
using System.Text;

public class MoreTests
{
    [Fact]
    public void DeterministicParameters_8192Iterations()
    {
        for (int i = 0; i < 8192; i++)
        {
            bool noDelay = (i & 1) != 0;
            int sendWindow = GetWindowFromBits((i >> 1) & 3);
            int recvWindow = GetWindowFromBits((i >> 3) & 3);
            int mtu = GetMtuFromBits((i >> 5) & 3);
            int interval = GetIntervalFromBits((i >> 7) & 3);
            int fastResend = ((i >> 9) & 3);
            bool streamMode = ((i >> 11) & 1) != 0;
            bool noCongestion = ((i >> 12) & 1) != 0;

            uint conversationId = (uint)i + 1;

            var kcp1 = new KcpManaged(conversationId);
            var kcp2 = new KcpManaged(conversationId);

            try
            {
                kcp1.SetWindowSize(sendWindow, recvWindow);
                kcp2.SetWindowSize(sendWindow, recvWindow);
                kcp1.SetMtu(mtu);
                kcp2.SetMtu(mtu);
                kcp1.SetNoDelay(noDelay, interval, fastResend, noCongestion);
                kcp2.SetNoDelay(noDelay, interval, fastResend, noCongestion);
                kcp1.IsStreamMode = streamMode;
                kcp2.IsStreamMode = streamMode;

                var packets1to2 = new List<byte[]>();
                var packets2to1 = new List<byte[]>();

                kcp1.OnOutput = (data) =>
                {
                    packets1to2.Add(data.ToArray());
                    return data.Length;
                };

                kcp2.OnOutput = (data) =>
                {
                    packets2to1.Add(data.ToArray());
                    return data.Length;
                };

                var testMessage = RandomNumberGenerator.GetBytes(50);
                uint time = 1000;

                kcp1.Update(time);
                kcp2.Update(time);

                kcp1.Send(testMessage);
                kcp1.Flush();

                for (int iter = 0; iter < 1000 && 0 < packets1to2.Count; iter++)
                {
                    var packetsToDeliver = packets1to2.ToList();
                    packets1to2.Clear();
                    foreach (var packet in packetsToDeliver)
                        kcp2.Input(packet);

                    kcp2.Update(time);
                    kcp2.Flush();

                    var acksToDeliver = packets2to1.ToList();
                    packets2to1.Clear();
                    foreach (var ack in acksToDeliver)
                        kcp1.Input(ack);

                    kcp1.Update(time);
                    kcp1.Flush();

                    time += (uint)interval;
                }

                var buffer = new byte[testMessage.Length + 100];
                int received = kcp2.Receive(buffer);

                Assert.True(received > 0,
                    $"Failed at i={i} (binary: {Convert.ToString(i, 2).PadLeft(12, '0')}): " +
                    $"noDelay={noDelay}, sendWindow={sendWindow}, recvWindow={recvWindow}, " +
                    $"mtu={mtu}, interval={interval}, fastResend={fastResend}, streamMode={streamMode}");
                Assert.Equal(testMessage, buffer.Take(received).ToArray());
            }
            finally
            {
                kcp1.Dispose();
                kcp2.Dispose();
            }
        }
    }

    [Fact]
    public async Task DeterministicParameters_TransportLevel_256Iterations()
    {
        for (int i = 0; i < 256; i++)
        {
            bool noDelay = (i & 1) != 0;
            int sendWindow = GetWindowFromBits((i >> 1) & 3);
            int recvWindow = GetWindowFromBits((i >> 3) & 3);
            int interval = GetIntervalFromBits((i >> 5) & 3);
            bool streamMode = ((i >> 7) & 1) != 0;
            int fastResend = ((i >> 8) & 1);

            uint conversationId = (uint)i + 100000;

            using var client1 = new TestTransport(conversationId, 1.0);
            using var client2 = new TestTransport(conversationId, 1.0);

            client1.SetWindowSize(sendWindow, recvWindow);
            client2.SetWindowSize(sendWindow, recvWindow);
            client1.SetInterval(interval);
            client2.SetInterval(interval);
            client1.SetNoDelay(noDelay, interval, fastResend, true);
            client2.SetNoDelay(noDelay, interval, fastResend, true);
            client1.SetStreamMode(streamMode);
            client2.SetStreamMode(streamMode);

            client1.AnotherTransport = client2;
            client2.AnotherTransport = client1;

            client1.Start();
            client2.Start();

            var testData = RandomNumberGenerator.GetBytes(8192);
            client1.Write(testData);

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            try
            {
                if (streamMode)
                {
                    var stream = client2.GetStream();
                    var buffer = new byte[testData.Length];
                    await stream.ReadExactlyAsync(buffer, cts.Token);
                    Assert.Equal(testData, buffer);
                }
                else
                {
                    var received = await client2.ReadAsync(cts.Token);
                    Assert.Equal(testData, received.ToArray());
                }
            }
            catch (OperationCanceledException)
            {
                throw new Xunit.Sdk.XunitException(
                    $"Timeout at i={i} (binary: {Convert.ToString(i, 2).PadLeft(9, '0')}): " +
                    $"noDelay={noDelay}, sendWindow={sendWindow}, recvWindow={recvWindow}, " +
                    $"interval={interval}, streamMode={streamMode}, fastResend={fastResend}");
            }

            await client1.StopAsync();
            await client2.StopAsync();
        }
    }

    [Fact]
    public async Task LongBidirectionalConversation_1000Messages()
    {
        using var client1 = new TestTransport(111111, 1.0);
        using var client2 = new TestTransport(111111, 1.0);

        client1.SetWindowSize(512, 512);
        client2.SetWindowSize(512, 512);
        client1.SetNoDelay(true, 10, 2, false);
        client2.SetNoDelay(true, 10, 2, false);

        client1.AnotherTransport = client2;
        client2.AnotherTransport = client1;

        client1.Start();
        client2.Start();

        var messages1to2 = new List<byte[]>();
        var messages2to1 = new List<byte[]>();
        var received1to2 = new List<byte[]>();
        var received2to1 = new List<byte[]>();

        for (int i = 0; i < 1000; i++)
        {
            messages1to2.Add(Encoding.UTF8.GetBytes($"Client1->Client2: Message {i}"));
            messages2to1.Add(Encoding.UTF8.GetBytes($"Client2->Client1: Message {i}"));
        }

        var sendTask1 = Task.Run(async () =>
        {
            foreach (var msg in messages1to2)
            {
                client1.Write(msg);
                await Task.Delay(1);
            }
        });

        var sendTask2 = Task.Run(async () =>
        {
            foreach (var msg in messages2to1)
            {
                client2.Write(msg);
                await Task.Delay(1);
            }
        });

        var receiveTask1 = Task.Run(async () =>
        {
            while (received2to1.Count < messages2to1.Count)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var data = await client1.ReadAsync(cts.Token);
                    if (!data.IsEmpty)
                        received2to1.Add(data.ToArray());
                }
                catch { break; }
            }
        });

        var receiveTask2 = Task.Run(async () =>
        {
            while (received1to2.Count < messages1to2.Count)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var data = await client2.ReadAsync(cts.Token);
                    if (!data.IsEmpty)
                        received1to2.Add(data.ToArray());
                }
                catch { break; }
            }
        });

        await Task.WhenAll(sendTask1, sendTask2, receiveTask1, receiveTask2);

        Assert.Equal(messages1to2, received1to2);
        Assert.Equal(messages2to1, received2to1);

        await client1.StopAsync();
        await client2.StopAsync();
    }

    [Fact]
    public async Task LongBidirectionalConversation_1000Messages_LossyNetwork()
    {
        using var client1 = new TestTransport(222222, 0.85);
        using var client2 = new TestTransport(222222, 0.85);

        client1.SetWindowSize(1024, 1024);
        client2.SetWindowSize(1024, 1024);
        client1.SetNoDelay(true, 10, 2, false);
        client2.SetNoDelay(true, 10, 2, false);

        client1.AnotherTransport = client2;
        client2.AnotherTransport = client1;

        client1.Start();
        client2.Start();

        const int messageCount = 1000;
        var messages1to2 = new List<byte[]>();
        var messages2to1 = new List<byte[]>();
        var received1to2 = new List<byte[]>();
        var received2to1 = new List<byte[]>();

        for (int i = 0; i < messageCount; i++)
        {
            messages1to2.Add(BitConverter.GetBytes(i));
            messages2to1.Add(BitConverter.GetBytes(i + messageCount));
        }

        var sendTask1 = Task.Run(async () =>
        {
            foreach (var msg in messages1to2)
            {
                client1.Write(msg);
                await Task.Delay(1);
            }
        });

        var sendTask2 = Task.Run(async () =>
        {
            foreach (var msg in messages2to1)
            {
                client2.Write(msg);
                await Task.Delay(1);
            }
        });

        var receiveTask1 = Task.Run(async () =>
        {
            while (received2to1.Count < messages2to1.Count)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                    var data = await client1.ReadAsync(cts.Token);
                    if (!data.IsEmpty)
                        received2to1.Add(data.ToArray());
                }
                catch { break; }
            }
        });

        var receiveTask2 = Task.Run(async () =>
        {
            while (received1to2.Count < messages1to2.Count)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                    var data = await client2.ReadAsync(cts.Token);
                    if (!data.IsEmpty)
                        received1to2.Add(data.ToArray());
                }
                catch { break; }
            }
        });

        await Task.WhenAll(sendTask1, sendTask2, receiveTask1, receiveTask2);

        Assert.Equal(messageCount, received1to2.Count);
        Assert.Equal(messageCount, received2to1.Count);
        Assert.Equal(messages1to2, received1to2);
        Assert.Equal(messages2to1, received2to1);

        await client1.StopAsync();
        await client2.StopAsync();
    }

    [Fact]
    public async Task AlternatingBidirectionalConversation_1000Rounds()
    {
        using var client1 = new TestTransport(444444, 1.0);
        using var client2 = new TestTransport(444444, 1.0);

        client1.SetWindowSize(256, 256);
        client2.SetWindowSize(256, 256);
        client1.SetNoDelay(true, 10, 2, false);
        client2.SetNoDelay(true, 10, 2, false);

        client1.AnotherTransport = client2;
        client2.AnotherTransport = client1;

        client1.Start();
        client2.Start();

        const int rounds = 1000;

        for (int i = 0; i < rounds; i++)
        {
            var msg1 = Encoding.UTF8.GetBytes($"Round {i}: Client1 -> Client2");
            client1.Write(msg1);
            var received1 = await client2.ReadAsync();
            Assert.Equal(msg1, received1.ToArray());

            var msg2 = Encoding.UTF8.GetBytes($"Round {i}: Client2 -> Client1");
            client2.Write(msg2);
            var received2 = await client1.ReadAsync();
            Assert.Equal(msg2, received2.ToArray());
        }

        await client1.StopAsync();
        await client2.StopAsync();
    }

    [Fact]
    public void SequentialDisposeAndInstantiate_100Iterations()
    {
        for (int i = 0; i < 100; i++)
        {
            var kcp = new KcpManaged((uint)i + 1);
            kcp.SetWindowSize(128, 128);
            kcp.SetNoDelay(true, 10, 2, false);

            var data = Encoding.UTF8.GetBytes($"Test message {i}");
            var received = false;

            kcp.OnOutput = (outputData) =>
            {
                received = true;
                return outputData.Length;
            };

            kcp.Update(1000);
            kcp.Send(data);
            kcp.Flush();

            Assert.True(received);
            kcp.Dispose();
            Assert.Throws<ObjectDisposedException>(() => kcp.Send(data));
        }
    }

    [Fact]
    public void MultipleInstancesInOneRun_50Instances()
    {
        var instances = new List<KcpManaged>();
        var outputs = new Dictionary<uint, List<byte[]>>();

        try
        {
            for (uint i = 1; i <= 50; i++)
            {
                var kcp = new KcpManaged(i);
                kcp.SetWindowSize(64, 64);
                kcp.SetNoDelay(true, 10, 2, true);

                outputs[i] = new List<byte[]>();
                var convId = i;
                kcp.OnOutput = (data) =>
                {
                    outputs[convId].Add(data.ToArray());
                    return data.Length;
                };

                instances.Add(kcp);
            }

            for (int i = 0; i < instances.Count; i++)
            {
                var kcp = instances[i];
                kcp.Update(1000);
                kcp.Send(BitConverter.GetBytes(i));
                kcp.Flush();
            }

            for (uint i = 1; i <= 50; i++)
            {
                Assert.NotEmpty(outputs[i]);
            }

            for (int i = 0; i < 25; i++)
            {
                instances[i].Dispose();
            }

            for (int i = 0; i < 25; i++)
            {
                Assert.Throws<ObjectDisposedException>(() => instances[i].Send(new byte[10]));
            }

            for (int i = 25; i < instances.Count; i++)
            {
                outputs[(uint)(i + 1)].Clear();
                instances[i].Send(BitConverter.GetBytes(i + 100));
                instances[i].Flush();
                Assert.NotEmpty(outputs[(uint)(i + 1)]);
            }
        }
        finally
        {
            foreach (var kcp in instances)
            {
                kcp.Dispose();
            }
        }
    }

    [Fact]
    public async Task TransportDisposeAndInstantiate_20Iterations()
    {
        for (int i = 0; i < 20; i++)
        {
            using var client1 = new TestTransport((uint)i * 2 + 1, 1.0);
            using var client2 = new TestTransport((uint)i * 2 + 1, 1.0);

            client1.SetWindowSize(128, 128);
            client2.SetWindowSize(128, 128);
            client1.SetNoDelay(true, 10, 2, false);
            client2.SetNoDelay(true, 10, 2, false);

            client1.AnotherTransport = client2;
            client2.AnotherTransport = client1;

            client1.Start();
            client2.Start();

            var testData = Encoding.UTF8.GetBytes($"Iteration {i} test message");
            client1.Write(testData);

            var received = await client2.ReadAsync();
            Assert.Equal(testData, received.ToArray());

            await client1.StopAsync();
            await client2.StopAsync();
        }
    }

    [Fact]
    public void RapidCreateDispose_1000Iterations()
    {
        for (int i = 0; i < 1000; i++)
        {
            var kcp = new KcpManaged((uint)i + 1);
            kcp.Dispose();
        }
    }

    [Fact]
    public void DisposeMultipleTimes_NoException()
    {
        var kcp = new KcpManaged(12345);
        kcp.Dispose();
        kcp.Dispose();
        kcp.Dispose();
    }

    [Fact]
    public async Task ConcurrentCreateDispose_100Tasks()
    {
        var tasks = new List<Task>();

        for (int i = 0; i < 100; i++)
        {
            var taskIndex = i;
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 10; j++)
                {
                    var kcp = new KcpManaged((uint)(taskIndex * 100 + j));
                    kcp.SetWindowSize(32, 32);
                    kcp.SetNoDelay(true, 10, 2, false);
                    kcp.Update(1000);
                    kcp.Send(BitConverter.GetBytes(taskIndex));
                    kcp.Dispose();
                }
            }));
        }

        await Task.WhenAll(tasks);
    }

    [Fact]
    public void StressTest_CreateUseDispose_Pattern()
    {
        for (int iteration = 0; iteration < 100; iteration++)
        {
            var kcp1 = new KcpManaged((uint)iteration + 1);
            var kcp2 = new KcpManaged((uint)iteration + 1);

            kcp1.SetWindowSize(128, 128);
            kcp2.SetWindowSize(128, 128);
            kcp1.SetNoDelay(true, 10, 2, false);
            kcp2.SetNoDelay(true, 10, 2, false);

            var packets = new List<byte[]>();
            kcp1.OnOutput = (data) =>
            {
                packets.Add(data.ToArray());
                return data.Length;
            };

            var message = RandomNumberGenerator.GetBytes(100);
            kcp1.Update(1000);
            kcp1.Send(message);
            kcp1.Flush();

            kcp2.Update(1000);
            foreach (var packet in packets)
            {
                kcp2.Input(packet);
            }
            kcp2.Update(2000);

            var buffer = new byte[200];
            var received = kcp2.Receive(buffer);
            Assert.True(received > 0);

            kcp1.Dispose();
            kcp2.Dispose();

            Assert.Throws<ObjectDisposedException>(() => kcp1.Send(new byte[10]));
            Assert.Throws<ObjectDisposedException>(() => kcp2.Send(new byte[10]));
        }
    }

    [Fact]
    public async Task MixedOperationsWithDispose_50Rounds()
    {
        for (int round = 0; round < 50; round++)
        {
            using var client1 = new TestTransport((uint)round + 100000, 0.9);
            using var client2 = new TestTransport((uint)round + 100000, 0.9);

            client1.SetWindowSize(256, 256);
            client2.SetWindowSize(256, 256);
            client1.SetNoDelay(true, 10, 2, false);
            client2.SetNoDelay(true, 10, 2, false);

            client1.AnotherTransport = client2;
            client2.AnotherTransport = client1;

            client1.Start();
            client2.Start();

            var messages1to2 = new List<byte[]>();
            var messages2to1 = new List<byte[]>();

            for (int i = 0; i < 20; i++)
            {
                var msg1 = RandomNumberGenerator.GetBytes(50 + i);
                var msg2 = RandomNumberGenerator.GetBytes(50 + i);
                messages1to2.Add(msg1);
                messages2to1.Add(msg2);
                client1.Write(msg1);
                client2.Write(msg2);
            }

            var received1to2 = new List<byte[]>();
            var received2to1 = new List<byte[]>();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            while (received1to2.Count < 20 || received2to1.Count < 20)
            {
                try
                {
                    if (received1to2.Count < 20)
                    {
                        var data = await client2.ReadAsync(cts.Token);
                        if (!data.IsEmpty)
                            received1to2.Add(data.ToArray());
                    }

                    if (received2to1.Count < 20)
                    {
                        var data = await client1.ReadAsync(cts.Token);
                        if (!data.IsEmpty)
                            received2to1.Add(data.ToArray());
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            Assert.Equal(messages1to2, received1to2);
            Assert.Equal(messages2to1, received2to1);

            await client1.StopAsync();
            await client2.StopAsync();
        }
    }

    [Fact]
    public void DisposeDuringActiveOperation()
    {
        for (int i = 0; i < 100; i++)
        {
            var kcp = new KcpManaged((uint)i + 1);
            kcp.SetWindowSize(512, 512);
            kcp.SetNoDelay(true, 10, 2, false);

            var packets = new List<byte[]>();
            kcp.OnOutput = (data) =>
            {
                packets.Add(data.ToArray());
                return data.Length;
            };

            for (int j = 0; j < 100; j++)
            {
                kcp.Update((uint)(1000 + j * 10));
                kcp.Send(RandomNumberGenerator.GetBytes(500));
            }

            kcp.Dispose();
        }
    }

    [Fact]
    public async Task TransportDisposeWhileActive_NoDeadlocks()
    {
        for (int i = 0; i < 30; i++)
        {
            using var client1 = new TestTransport((uint)i + 200000, 1.0);
            using var client2 = new TestTransport((uint)i + 200000, 1.0);

            client1.SetWindowSize(1024, 1024);
            client2.SetWindowSize(1024, 1024);

            client1.AnotherTransport = client2;
            client2.AnotherTransport = client1;

            client1.Start();
            client2.Start();

            var sendTask = Task.Run(async () =>
            {
                for (int j = 0; j < 100; j++)
                {
                    try
                    {
                        client1.Write(BitConverter.GetBytes(j));
                    }
                    catch (IOException)
                    {
                        break;
                    }
                    await Task.Delay(5);
                }
            });

            await Task.Delay(50);

            await client1.StopAsync();
            await client2.StopAsync();

            await sendTask;
        }
    }

    private static int GetWindowFromBits(int bits) => bits switch
    {
        0 => 32,
        1 => 64,
        2 => 256,
        3 => 1024,
        _ => 2048
    };

    private static int GetMtuFromBits(int bits) => bits switch
    {
        0 => 100,
        1 => 500,
        2 => 1000,
        3 => 1400,
        _ => 100
    };

    private static int GetIntervalFromBits(int bits) => bits switch
    {
        0 => 10,
        1 => 50,
        2 => 100,
        3 => 200,
        _ => 10
    };

    public class TestTransport : KcpTransport
    {
        public TestTransport? AnotherTransport { get; set; }

        private readonly double _successChance;

        public System.Threading.Channels.Channel<byte[]> Channel { get; } =
            System.Threading.Channels.Channel.CreateUnbounded<byte[]>();

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
