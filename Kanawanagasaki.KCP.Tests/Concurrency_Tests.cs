namespace Kanawanagasaki.KCP.Tests;

using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

public class Concurrency_Tests
{
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

    [Fact]
    public async Task ConcurrentInputAndWrite()
    {
        using var client1 = new TestTransport(300101, 1.0);
        using var client2 = new TestTransport(300101, 1.0);

        client1.SetWindowSize(512, 512);
        client2.SetWindowSize(512, 512);
        client1.SetNoDelay(true, 10, 2, true);
        client2.SetNoDelay(true, 10, 2, true);

        client1.AnotherTransport = client2;
        client2.AnotherTransport = client1;

        client1.Start();
        client2.Start();

        const int messageCount = 200;
        var sendErrors = new List<Exception>();

        var sendTask = Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < messageCount; i++)
                    client1.Write(BitConverter.GetBytes(i));
            }
            catch (Exception ex)
            {
                lock (sendErrors)
                    sendErrors.Add(ex);
            }
        });

        var received = new List<byte[]>();
        var recvTask = Task.Run(async () =>
        {
            while (received.Count < messageCount)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var data = await client2.ReadAsync(cts.Token);
                    if (!data.IsEmpty)
                        lock (received)
                            received.Add(data.ToArray());
                }
                catch
                {
                    break;
                }
            }
        });

        await sendTask;
        await recvTask;

        Assert.Empty(sendErrors);
        Assert.Equal(messageCount, received.Count);

        await client1.StopAsync();
        await client2.StopAsync();
    }

    [Fact]
    public async Task A1000SynchronousSends_BigWindow()
    {
        using var client1 = new TestTransport(300200, 1.0);
        using var client2 = new TestTransport(300200, 1.0);

        client1.SetWindowSize(2048, 2048);
        client2.SetWindowSize(2048, 2048);
        client1.SetNoDelay(true, 10, 2, true);
        client2.SetNoDelay(true, 10, 2, true);

        client1.AnotherTransport = client2;
        client2.AnotherTransport = client1;

        client1.Start();
        client2.Start();

        const int messageCount = 1000;
        var messages = new byte[messageCount][];
        for (int i = 0; i < messageCount; i++)
            messages[i] = BitConverter.GetBytes(i);

        var sendTasks = new Task[messageCount];
        for (int i = 0; i < messageCount; i++)
        {
            var idx = i;
            sendTasks[i] = Task.Run(() => client1.Write(messages[idx]));
        }

        await Task.WhenAll(sendTasks);

        var received = new List<byte[]>();
        using var recvCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (received.Count < messageCount)
        {
            try
            {
                var data = await client2.ReadAsync(recvCts.Token);
                if (!data.IsEmpty)
                    received.Add(data.ToArray());
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Assert.Equal(messageCount, received.Count);
        for (int i = 0; i < messageCount; i++)
            Assert.Contains(BitConverter.GetBytes(i), received);

        await client1.StopAsync();
        await client2.StopAsync();
    }

    [Fact]
    public async Task A1000SynchronousSends_SmallWindow()
    {
        using var client1 = new TestTransport(300201, 1.0);
        using var client2 = new TestTransport(300201, 1.0);

        client1.SetWindowSize(64, 64);
        client2.SetWindowSize(64, 64);
        client1.SetNoDelay(true, 10, 2, true);
        client2.SetNoDelay(true, 10, 2, true);

        client1.AnotherTransport = client2;
        client2.AnotherTransport = client1;

        client1.Start();
        client2.Start();

        const int messageCount = 1000;
        var errors = new List<Exception>();
        var successCount = 0;

        var sendTasks = new Task[messageCount];
        for (int i = 0; i < messageCount; i++)
        {
            var idx = i;
            sendTasks[i] = Task.Run(() =>
            {
                try
                {
                    client1.Write(BitConverter.GetBytes(idx));
                    Interlocked.Increment(ref successCount);
                }
                catch (Exception ex)
                {
                    lock (errors) errors.Add(ex);
                }
            });
        }

        await Task.WhenAll(sendTasks);

        Assert.True(successCount > 0, $"Expected some sends to succeed, but got {successCount}");

        var received = new List<byte[]>();
        using var recvCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (received.Count < successCount)
        {
            try
            {
                var data = await client2.ReadAsync(recvCts.Token);
                if (!data.IsEmpty)
                    received.Add(data.ToArray());
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Assert.Equal(successCount, received.Count);

        await client1.StopAsync();
        await client2.StopAsync();
    }

    [Fact]
    public async Task A1000ConcurrentSends_MessageMode()
    {
        using var client1 = new TestTransport(300400, 1.0);
        using var client2 = new TestTransport(300400, 1.0);

        client1.SetWindowSize(4096, 4096);
        client2.SetWindowSize(4096, 4096);
        client1.SetNoDelay(true, 10, 2, true);
        client2.SetNoDelay(true, 10, 2, true);

        client1.AnotherTransport = client2;
        client2.AnotherTransport = client1;

        client1.Start();
        client2.Start();

        const int messageCount = 1000;
        var messages = new byte[messageCount][];
        for (int i = 0; i < messageCount; i++)
            messages[i] = RandomNumberGenerator.GetBytes(Random.Shared.Next(8, 64));
        var sendTasks = new Task[messageCount];
        for (int i = 0; i < messageCount; i++)
        {
            var idx = i;
            sendTasks[idx] = Task.Run(() => client1.Write(messages[idx]));
        }

        await Task.WhenAll(sendTasks);
        var received = new List<byte[]>();
        using var recvCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (received.Count < messageCount)
        {
            try
            {
                var data = await client2.ReadAsync(recvCts.Token);
                if (!data.IsEmpty)
                    received.Add(data.ToArray());
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Assert.Equal(messageCount, received.Count);
        foreach (var msg in messages)
            Assert.Contains(msg, received);

        await client1.StopAsync();
        await client2.StopAsync();
    }

    [Fact]
    public async Task DisposeWhileSending()
    {
        var errors = new List<Exception>();

        for (int i = 0; i < 50; i++)
        {
            using var client1 = new TestTransport((uint)(300403 + i), 1.0);
            using var client2 = new TestTransport((uint)(300403 + i), 1.0);

            client1.SetWindowSize(1024, 1024);
            client2.SetWindowSize(1024, 1024);

            client1.AnotherTransport = client2;
            client2.AnotherTransport = client1;

            client1.Start();
            client2.Start();
            _ = Task.Run(() =>
                    {
                        try
                        {
                            for (int j = 0; j < 100; j++)
                                client1.Write(BitConverter.GetBytes(j));
                        }
                        catch (Exception ex)
                        {
                            lock (errors) errors.Add(ex);
                        }
                    });

            await Task.Delay(Random.Shared.Next(5, 20));
            client1.Dispose();
            client2.Dispose();
        }
        Assert.All(errors, ex => Assert.True(
                    ex is ObjectDisposedException or IOException || ex.GetType().Name == "SendWindowExceededException",
                    $"Unexpected exception type: {ex.GetType().Name}: {ex.Message}"));
    }

    [Fact]
    public async Task FlushDuringActiveReceive()
    {
        using var client1 = new TestTransport(300404, 1.0);
        using var client2 = new TestTransport(300404, 1.0);

        client1.SetWindowSize(512, 512);
        client2.SetWindowSize(512, 512);
        client1.SetNoDelay(true, 10, 2, true);
        client2.SetNoDelay(true, 10, 2, true);

        client1.AnotherTransport = client2;
        client2.AnotherTransport = client1;

        client1.Start();
        client2.Start();

        var messages = new byte[100][];
        for (int i = 0; i < messages.Length; i++)
            messages[i] = Encoding.UTF8.GetBytes($"Message {i} with extra data to fill");
        var sendTask = Task.Run(() =>
                {
                    foreach (var msg in messages)
                    {
                        client1.Write(msg);
                        client1.Flush();
                    }
                });

        var received = new List<byte[]>();
        using var recvCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (received.Count < messages.Length)
        {
            try
            {
                var data = await client2.ReadAsync(recvCts.Token);
                if (!data.IsEmpty)
                    received.Add(data.ToArray());
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await sendTask;
        Assert.Equal(messages.Length, received.Count);

        await client1.StopAsync();
        await client2.StopAsync();
    }
}
