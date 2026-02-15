namespace Kanawanagasaki.KCP.Tests;

using Microsoft.VisualStudio.TestPlatform.Utilities;
using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using System.Threading.Tasks;
using Xunit.Abstractions;

public class PerlinNoiseTests(ITestOutputHelper _output)
{
    private const bool NoDelay = true;
    private const int Interval = 10;
    private const int FastResend = 2;
    private const bool NoCongestion = false;
    private const int WindowSize = 512;

    [Fact]
    public unsafe void LowLevelKcp_PingPong_1000Messages()
    {
        var simulator1to2 = new PerlinNetworkSimulator(seed: 42, baseDropRate: 0.15f, variationAmplitude: 0.10f);
        var simulator2to1 = new PerlinNetworkSimulator(seed: 24, baseDropRate: 0.15f, variationAmplitude: 0.10f);

        var packets1to2 = new List<byte[]>();
        var packets2to1 = new List<byte[]>();

        var kcp1 = KCP.ikcp_create(111111, null);
        var kcp2 = KCP.ikcp_create(111111, null);

        try
        {
            KCP.ikcp_wndsize(kcp1, WindowSize, WindowSize);
            KCP.ikcp_wndsize(kcp2, WindowSize, WindowSize);
            KCP.ikcp_nodelay(kcp1, NoDelay ? 1 : 0, Interval, FastResend, NoCongestion ? 1 : 0);
            KCP.ikcp_nodelay(kcp2, NoDelay ? 1 : 0, Interval, FastResend, NoCongestion ? 1 : 0);
            kcp1->stream = 0;
            kcp2->stream = 0;

            KCP.ikcp_setoutput(kcp1, &Kcp1OutputCallback);
            KCP.ikcp_setoutput(kcp2, &Kcp2OutputCallback);

            KcpTestContext.Current1 = packets1to2;
            KcpTestContext.Current2 = packets2to1;

            uint time = 1000;
            const int messageCount = 1000;
            var messagesReceivedBy2 = new List<byte[]>();
            var messagesReceivedBy1 = new List<byte[]>();
            int msgIndex = 0;

            KCP.ikcp_update(kcp1, time);
            KCP.ikcp_update(kcp2, time);

            while (msgIndex < messageCount || messagesReceivedBy2.Count < messageCount || messagesReceivedBy1.Count < messageCount)
            {
                if (msgIndex < messageCount && (msgIndex == 0 || messagesReceivedBy1.Count >= msgIndex))
                {
                    var msg = BitConverter.GetBytes(msgIndex);
                    fixed (byte* ptr = msg)
                    {
                        KCP.ikcp_send(kcp1, ptr, msg.Length);
                    }
                    KCP.ikcp_flush(kcp1);
                    msgIndex++;
                }

                simulator1to2.AdvanceTime(Interval);
                simulator2to1.AdvanceTime(Interval);
                time += Interval;

                foreach (var packet in packets1to2)
                {
                    if (!simulator1to2.ShouldDropPacket())
                    {
                        fixed (byte* ptr = packet)
                        {
                            KCP.ikcp_input(kcp2, ptr, packet.Length);
                        }
                    }
                }
                packets1to2.Clear();

                foreach (var packet in packets2to1)
                {
                    if (!simulator2to1.ShouldDropPacket())
                    {
                        fixed (byte* ptr = packet)
                        {
                            KCP.ikcp_input(kcp1, ptr, packet.Length);
                        }
                    }
                }
                packets2to1.Clear();

                KCP.ikcp_update(kcp1, time);
                KCP.ikcp_update(kcp2, time);

                var buffer = new byte[100];
                fixed (byte* ptr = buffer)
                {
                    int recvSize;
                    while ((recvSize = KCP.ikcp_recv(kcp2, ptr, buffer.Length)) > 0)
                    {
                        var received = new byte[recvSize];
                        Marshal.Copy((IntPtr)ptr, received, 0, recvSize);
                        messagesReceivedBy2.Add(received);

                        var response = BitConverter.GetBytes(BitConverter.ToInt32(received) + 1000000);
                        fixed (byte* respPtr = response)
                        {
                            KCP.ikcp_send(kcp2, respPtr, response.Length);
                        }
                    }
                }
                KCP.ikcp_flush(kcp2);

                fixed (byte* ptr = buffer)
                {
                    int recvSize;
                    while ((recvSize = KCP.ikcp_recv(kcp1, ptr, buffer.Length)) > 0)
                    {
                        var received = new byte[recvSize];
                        Marshal.Copy((IntPtr)ptr, received, 0, recvSize);
                        messagesReceivedBy1.Add(received);
                    }
                }

                if (time > 1000 + (uint)(messageCount * 500 * Interval))
                    break;
            }

            _output.WriteLine($"=== Test 1: Low Level KCP Ping Pong 1000 Messages ===");
            _output.WriteLine($"Direction 1->2: {simulator1to2.GetStatistics()}");
            _output.WriteLine($"Direction 2->1: {simulator2to1.GetStatistics()}");
            _output.WriteLine($"Total packets: {simulator1to2.TotalPackets + simulator2to1.TotalPackets}");
            _output.WriteLine($"Total dropped: {simulator1to2.DroppedPackets + simulator2to1.DroppedPackets}");
            _output.WriteLine($"Overall drop rate: {(simulator1to2.TotalPackets + simulator2to1.TotalPackets > 0 ? (double)(simulator1to2.DroppedPackets + simulator2to1.DroppedPackets) / (simulator1to2.TotalPackets + simulator2to1.TotalPackets) * 100 : 0):F2}%");

            Assert.Equal(messageCount, messagesReceivedBy2.Count);
            Assert.Equal(messageCount, messagesReceivedBy1.Count);

            for (int i = 0; i < messageCount; i++)
            {
                Assert.Equal(i, BitConverter.ToInt32(messagesReceivedBy2[i]));
                Assert.Equal(i + 1000000, BitConverter.ToInt32(messagesReceivedBy1[i]));
            }
        }
        finally
        {
            KcpTestContext.Current1 = null;
            KcpTestContext.Current2 = null;
            KCP.ikcp_release(kcp1);
            KCP.ikcp_release(kcp2);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int Kcp1OutputCallback(byte* data, int size, IKCPCB* kcp, void* user)
    {
        var buffer = new byte[size];
        Marshal.Copy((IntPtr)data, buffer, 0, size);
        KcpTestContext.Current1?.Add(buffer);
        return size;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int Kcp2OutputCallback(byte* data, int size, IKCPCB* kcp, void* user)
    {
        var buffer = new byte[size];
        Marshal.Copy((IntPtr)data, buffer, 0, size);
        KcpTestContext.Current2?.Add(buffer);
        return size;
    }

    [Fact]
    public void KcpManaged_PingPong_1000Messages()
    {
        var simulator1to2 = new PerlinNetworkSimulator(seed: 123, baseDropRate: 0.15f, variationAmplitude: 0.10f);
        var simulator2to1 = new PerlinNetworkSimulator(seed: 321, baseDropRate: 0.15f, variationAmplitude: 0.10f);

        var kcp1 = new KcpManaged(222222);
        var kcp2 = new KcpManaged(222222);

        try
        {
            kcp1.SetWindowSize(WindowSize, WindowSize);
            kcp2.SetWindowSize(WindowSize, WindowSize);
            kcp1.SetNoDelay(NoDelay, Interval, FastResend, NoCongestion);
            kcp2.SetNoDelay(NoDelay, Interval, FastResend, NoCongestion);
            kcp1.IsStreamMode = false;
            kcp2.IsStreamMode = false;

            var packets1to2 = new List<byte[]>();
            var packets2to1 = new List<byte[]>();

            kcp1.OnOutput = data =>
            {
                packets1to2.Add(data.ToArray());
                return data.Length;
            };

            kcp2.OnOutput = data =>
            {
                packets2to1.Add(data.ToArray());
                return data.Length;
            };

            uint time = 1000;
            const int messageCount = 1000;
            var messagesReceivedBy2 = new List<byte[]>();
            var messagesReceivedBy1 = new List<byte[]>();
            int msgIndex = 0;

            kcp1.Update(time);
            kcp2.Update(time);

            while (msgIndex < messageCount || messagesReceivedBy2.Count < messageCount || messagesReceivedBy1.Count < messageCount)
            {
                if (msgIndex < messageCount && (msgIndex == 0 || messagesReceivedBy1.Count >= msgIndex))
                {
                    kcp1.Send(BitConverter.GetBytes(msgIndex));
                    kcp1.Flush();
                    msgIndex++;
                }

                simulator1to2.AdvanceTime(Interval);
                simulator2to1.AdvanceTime(Interval);
                time += Interval;

                foreach (var packet in packets1to2)
                {
                    if (!simulator1to2.ShouldDropPacket())
                        kcp2.Input(packet);
                }
                packets1to2.Clear();

                foreach (var packet in packets2to1)
                {
                    if (!simulator2to1.ShouldDropPacket())
                        kcp1.Input(packet);
                }
                packets2to1.Clear();

                kcp1.Update(time);
                kcp2.Update(time);

                var buffer = new byte[100];
                int recvSize;
                while ((recvSize = kcp2.Receive(buffer)) > 0)
                {
                    var received = buffer.AsSpan(0, recvSize).ToArray();
                    messagesReceivedBy2.Add(received);

                    var response = BitConverter.GetBytes(BitConverter.ToInt32(received) + 1000000);
                    kcp2.Send(response);
                }
                kcp2.Flush();

                while ((recvSize = kcp1.Receive(buffer)) > 0)
                {
                    messagesReceivedBy1.Add(buffer.AsSpan(0, recvSize).ToArray());
                }

                if (time > 1000 + (uint)(messageCount * 500 * Interval))
                    break;
            }

            _output.WriteLine($"=== Test 2: KcpManaged Ping Pong 1000 Messages ===");
            _output.WriteLine($"Direction 1->2: {simulator1to2.GetStatistics()}");
            _output.WriteLine($"Direction 2->1: {simulator2to1.GetStatistics()}");
            _output.WriteLine($"Total packets: {simulator1to2.TotalPackets + simulator2to1.TotalPackets}");
            _output.WriteLine($"Total dropped: {simulator1to2.DroppedPackets + simulator2to1.DroppedPackets}");
            _output.WriteLine($"Overall drop rate: {(simulator1to2.TotalPackets + simulator2to1.TotalPackets > 0 ? (double)(simulator1to2.DroppedPackets + simulator2to1.DroppedPackets) / (simulator1to2.TotalPackets + simulator2to1.TotalPackets) * 100 : 0):F2}%");

            Assert.Equal(messageCount, messagesReceivedBy2.Count);
            Assert.Equal(messageCount, messagesReceivedBy1.Count);

            for (int i = 0; i < messageCount; i++)
            {
                Assert.Equal(i, BitConverter.ToInt32(messagesReceivedBy2[i]));
                Assert.Equal(i + 1000000, BitConverter.ToInt32(messagesReceivedBy1[i]));
            }
        }
        finally
        {
            kcp1.Dispose();
            kcp2.Dispose();
        }
    }

    [Fact]
    public async Task KcpTransport_PingPong_1000Messages()
    {
        var simulator1to2 = new PerlinNetworkSimulator(seed: 456, baseDropRate: 0.12f, variationAmplitude: 0.08f);
        var simulator2to1 = new PerlinNetworkSimulator(seed: 654, baseDropRate: 0.12f, variationAmplitude: 0.08f);

        using var client1 = new PerlinTestTransport(333333, simulator1to2, simulator2to1);
        using var client2 = new PerlinTestTransport(333333, simulator2to1, simulator1to2);

        client1.SetWindowSize(WindowSize, WindowSize);
        client2.SetWindowSize(WindowSize, WindowSize);
        client1.SetNoDelay(NoDelay, Interval, FastResend, NoCongestion);
        client2.SetNoDelay(NoDelay, Interval, FastResend, NoCongestion);
        client1.SetStreamMode(false);
        client2.SetStreamMode(false);

        client1.OtherTransport = client2;
        client2.OtherTransport = client1;

        client1.Start();
        client2.Start();

        const int messageCount = 1000;
        var messagesReceivedBy2 = new List<byte[]>();
        var messagesReceivedBy1 = new List<byte[]>();
        int msgIndex = 0;
        int responsesPending = 0;

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        while ((msgIndex < messageCount || messagesReceivedBy1.Count < messageCount) && !cts.Token.IsCancellationRequested)
        {
            if (msgIndex < messageCount && (msgIndex == 0 || responsesPending < 10))
            {
                client1.Write(BitConverter.GetBytes(msgIndex));
                msgIndex++;
                responsesPending++;
            }

            try
            {
                var data = await client2.ReadAsync(cts.Token);
                if (!data.IsEmpty)
                {
                    messagesReceivedBy2.Add(data.ToArray());
                    var response = BitConverter.GetBytes(BitConverter.ToInt32(data.Span) + 1000000);
                    client2.Write(response);
                }
            }
            catch (OperationCanceledException) { break; }

            try
            {
                var data = await client1.ReadAsync(cts.Token);
                if (!data.IsEmpty)
                {
                    messagesReceivedBy1.Add(data.ToArray());
                    responsesPending--;
                }
            }
            catch (OperationCanceledException) { break; }
        }

        _output.WriteLine($"=== Test 3: KcpTransport Ping Pong 1000 Messages ===");
        _output.WriteLine($"Direction 1->2: {simulator1to2.GetStatistics()}");
        _output.WriteLine($"Direction 2->1: {simulator2to1.GetStatistics()}");
        _output.WriteLine($"Total packets: {simulator1to2.TotalPackets + simulator2to1.TotalPackets}");
        _output.WriteLine($"Total dropped: {simulator1to2.DroppedPackets + simulator2to1.DroppedPackets}");
        _output.WriteLine($"Overall drop rate: {(simulator1to2.TotalPackets + simulator2to1.TotalPackets > 0 ? (double)(simulator1to2.DroppedPackets + simulator2to1.DroppedPackets) / (simulator1to2.TotalPackets + simulator2to1.TotalPackets) * 100 : 0):F2}%");

        Assert.Equal(messageCount, messagesReceivedBy2.Count);
        Assert.Equal(messageCount, messagesReceivedBy1.Count);

        for (int i = 0; i < Math.Min(messageCount, Math.Min(messagesReceivedBy2.Count, messagesReceivedBy1.Count)); i++)
        {
            Assert.Equal(i, BitConverter.ToInt32(messagesReceivedBy2[i]));
            Assert.Equal(i + 1000000, BitConverter.ToInt32(messagesReceivedBy1[i]));
        }

        await client1.StopAsync();
        await client2.StopAsync();
    }

    [Fact]
    public unsafe void LowLevelKcp_BidirectionalStream_2MB()
    {
        const int dataSize = 2 * 1024 * 1024;

        var simulator1to2 = new PerlinNetworkSimulator(seed: 789, baseDropRate: 0.10f, variationAmplitude: 0.08f);
        var simulator2to1 = new PerlinNetworkSimulator(seed: 987, baseDropRate: 0.10f, variationAmplitude: 0.08f);

        var data1to2 = RandomNumberGenerator.GetBytes(dataSize);
        var data2to1 = RandomNumberGenerator.GetBytes(dataSize);

        var packets1to2 = new List<byte[]>();
        var packets2to1 = new List<byte[]>();

        var kcp1 = KCP.ikcp_create(444444, null);
        var kcp2 = KCP.ikcp_create(444444, null);

        try
        {
            KCP.ikcp_wndsize(kcp1, WindowSize, WindowSize);
            KCP.ikcp_wndsize(kcp2, WindowSize, WindowSize);
            KCP.ikcp_nodelay(kcp1, NoDelay ? 1 : 0, Interval, FastResend, NoCongestion ? 1 : 0);
            KCP.ikcp_nodelay(kcp2, NoDelay ? 1 : 0, Interval, FastResend, NoCongestion ? 1 : 0);
            kcp1->stream = 1;
            kcp2->stream = 1;

            KCP.ikcp_setoutput(kcp1, &Kcp1OutputCallback);
            KCP.ikcp_setoutput(kcp2, &Kcp2OutputCallback);

            KcpTestContext.Current1 = packets1to2;
            KcpTestContext.Current2 = packets2to1;

            uint time = 1000;
            KCP.ikcp_update(kcp1, time);
            KCP.ikcp_update(kcp2, time);

            var receivedBy1 = new List<byte>();
            var receivedBy2 = new List<byte[]>();
            var receiveBuffer = new byte[65536];

            int sent1to2 = 0;
            int sent2to1 = 0;
            int maxIterations = 1000000;
            int iteration = 0;
            int chunkSize = 8192;

            while ((receivedBy1.Count < dataSize || receivedBy2.Sum(x => x.Length) < dataSize) && iteration < maxIterations)
            {
                iteration++;
                simulator1to2.AdvanceTime(Interval);
                simulator2to1.AdvanceTime(Interval);
                time += Interval;

                if (sent1to2 < dataSize && KCP.ikcp_waitsnd(kcp1) < WindowSize - 10)
                {
                    int toSend = Math.Min(chunkSize, dataSize - sent1to2);
                    fixed (byte* ptr = data1to2.AsSpan(sent1to2, toSend))
                    {
                        int sent = KCP.ikcp_send(kcp1, ptr, toSend);
                        if (sent > 0)
                            sent1to2 += sent;
                    }
                }

                if (sent2to1 < dataSize && KCP.ikcp_waitsnd(kcp2) < WindowSize - 10)
                {
                    int toSend = Math.Min(chunkSize, dataSize - sent2to1);
                    fixed (byte* ptr = data2to1.AsSpan(sent2to1, toSend))
                    {
                        int sent = KCP.ikcp_send(kcp2, ptr, toSend);
                        if (sent > 0)
                            sent2to1 += sent;
                    }
                }

                KCP.ikcp_update(kcp1, time);
                KCP.ikcp_update(kcp2, time);
                KCP.ikcp_flush(kcp1);
                KCP.ikcp_flush(kcp2);

                foreach (var packet in packets1to2)
                {
                    if (!simulator1to2.ShouldDropPacket())
                    {
                        fixed (byte* ptr = packet)
                        {
                            KCP.ikcp_input(kcp2, ptr, packet.Length);
                        }
                    }
                }
                packets1to2.Clear();

                foreach (var packet in packets2to1)
                {
                    if (!simulator2to1.ShouldDropPacket())
                    {
                        fixed (byte* ptr = packet)
                        {
                            KCP.ikcp_input(kcp1, ptr, packet.Length);
                        }
                    }
                }
                packets2to1.Clear();

                fixed (byte* ptr = receiveBuffer)
                {
                    int recvSize;
                    while ((recvSize = KCP.ikcp_recv(kcp1, ptr, receiveBuffer.Length)) > 0)
                    {
                        for (int i = 0; i < recvSize; i++)
                            receivedBy1.Add(receiveBuffer[i]);
                    }

                    while ((recvSize = KCP.ikcp_recv(kcp2, ptr, receiveBuffer.Length)) > 0)
                    {
                        receivedBy2.Add(receiveBuffer.AsSpan(0, recvSize).ToArray());
                    }
                }
            }

            var totalReceivedBy1 = receivedBy1.Count;
            var totalReceivedBy2 = receivedBy2.Sum(x => x.Length);

            _output.WriteLine($"=== Test 4: Low Level KCP Bidirectional Stream 2MB ===");
            _output.WriteLine($"Direction 1->2: {simulator1to2.GetStatistics()}");
            _output.WriteLine($"Direction 2->1: {simulator2to1.GetStatistics()}");
            _output.WriteLine($"Total packets: {simulator1to2.TotalPackets + simulator2to1.TotalPackets}");
            _output.WriteLine($"Total dropped: {simulator1to2.DroppedPackets + simulator2to1.DroppedPackets}");
            _output.WriteLine($"Overall drop rate: {(simulator1to2.TotalPackets + simulator2to1.TotalPackets > 0 ? (double)(simulator1to2.DroppedPackets + simulator2to1.DroppedPackets) / (simulator1to2.TotalPackets + simulator2to1.TotalPackets) * 100 : 0):F2}%");
            _output.WriteLine($"Iterations: {iteration}");

            Assert.Equal(dataSize, totalReceivedBy1);
            Assert.Equal(dataSize, totalReceivedBy2);

            for (int i = 0; i < dataSize; i++)
            {
                Assert.Equal(data2to1[i], receivedBy1[i]);
            }

            int offset = 0;
            foreach (var chunk in receivedBy2)
            {
                for (int i = 0; i < chunk.Length; i++)
                {
                    Assert.Equal(data1to2[offset + i], chunk[i]);
                }
                offset += chunk.Length;
            }
        }
        finally
        {
            KcpTestContext.Current1 = null;
            KcpTestContext.Current2 = null;
            KCP.ikcp_release(kcp1);
            KCP.ikcp_release(kcp2);
        }
    }

    [Fact]
    public void KcpManaged_BidirectionalStream_2MB()
    {
        const int dataSize = 2 * 1024 * 1024;

        var simulator1to2 = new PerlinNetworkSimulator(seed: 111, baseDropRate: 0.10f, variationAmplitude: 0.08f);
        var simulator2to1 = new PerlinNetworkSimulator(seed: 222, baseDropRate: 0.10f, variationAmplitude: 0.08f);

        var data1to2 = RandomNumberGenerator.GetBytes(dataSize);
        var data2to1 = RandomNumberGenerator.GetBytes(dataSize);

        var kcp1 = new KcpManaged(555555);
        var kcp2 = new KcpManaged(555555);

        try
        {
            kcp1.SetWindowSize(WindowSize, WindowSize);
            kcp2.SetWindowSize(WindowSize, WindowSize);
            kcp1.SetNoDelay(NoDelay, Interval, FastResend, NoCongestion);
            kcp2.SetNoDelay(NoDelay, Interval, FastResend, NoCongestion);
            kcp1.IsStreamMode = true;
            kcp2.IsStreamMode = true;

            var packets1to2 = new List<byte[]>();
            var packets2to1 = new List<byte[]>();

            kcp1.OnOutput = data =>
            {
                packets1to2.Add(data.ToArray());
                return data.Length;
            };

            kcp2.OnOutput = data =>
            {
                packets2to1.Add(data.ToArray());
                return data.Length;
            };

            uint time = 1000;
            kcp1.Update(time);
            kcp2.Update(time);

            var receivedBy1 = new List<byte>();
            var receivedBy2Chunks = new List<byte[]>();
            var receiveBuffer = new byte[65536];

            int sent1to2 = 0;
            int sent2to1 = 0;
            int maxIterations = 1000000;
            int iteration = 0;
            int chunkSize = 8192;

            while ((receivedBy1.Count < dataSize || receivedBy2Chunks.Sum(x => x.Length) < dataSize) && iteration < maxIterations)
            {
                iteration++;
                simulator1to2.AdvanceTime(Interval);
                simulator2to1.AdvanceTime(Interval);
                time += Interval;

                if (sent1to2 < dataSize && kcp1.WaitSend() < WindowSize - 10)
                {
                    int toSend = Math.Min(chunkSize, dataSize - sent1to2);
                    int sent = kcp1.Send(data1to2.AsSpan(sent1to2, toSend));
                    if (sent > 0)
                    {
                        sent1to2 += sent;
                    }
                }

                if (sent2to1 < dataSize && kcp2.WaitSend() < WindowSize - 10)
                {
                    int toSend = Math.Min(chunkSize, dataSize - sent2to1);
                    int sent = kcp2.Send(data2to1.AsSpan(sent2to1, toSend));
                    if (sent > 0)
                    {
                        sent2to1 += sent;
                    }
                }

                kcp1.Update(time);
                kcp2.Update(time);
                kcp1.Flush();
                kcp2.Flush();

                foreach (var packet in packets1to2)
                {
                    if (!simulator1to2.ShouldDropPacket())
                        kcp2.Input(packet);
                }
                packets1to2.Clear();

                foreach (var packet in packets2to1)
                {
                    if (!simulator2to1.ShouldDropPacket())
                        kcp1.Input(packet);
                }
                packets2to1.Clear();

                int recvSize;
                while ((recvSize = kcp1.Receive(receiveBuffer)) > 0)
                {
                    for (int i = 0; i < recvSize; i++)
                        receivedBy1.Add(receiveBuffer[i]);
                }

                while ((recvSize = kcp2.Receive(receiveBuffer)) > 0)
                {
                    receivedBy2Chunks.Add(receiveBuffer.AsSpan(0, recvSize).ToArray());
                }
            }

            var totalReceivedBy2 = receivedBy2Chunks.Sum(x => x.Length);

            _output.WriteLine($"=== Test 5: KcpManaged Bidirectional Stream 2MB ===");
            _output.WriteLine($"Direction 1->2: {simulator1to2.GetStatistics()}");
            _output.WriteLine($"Direction 2->1: {simulator2to1.GetStatistics()}");
            _output.WriteLine($"Total packets: {simulator1to2.TotalPackets + simulator2to1.TotalPackets}");
            _output.WriteLine($"Total dropped: {simulator1to2.DroppedPackets + simulator2to1.DroppedPackets}");
            _output.WriteLine($"Overall drop rate: {(simulator1to2.TotalPackets + simulator2to1.TotalPackets > 0 ? (double)(simulator1to2.DroppedPackets + simulator2to1.DroppedPackets) / (simulator1to2.TotalPackets + simulator2to1.TotalPackets) * 100 : 0):F2}%");
            _output.WriteLine($"Iterations: {iteration}");

            Assert.Equal(dataSize, receivedBy1.Count);
            Assert.Equal(dataSize, totalReceivedBy2);

            for (int i = 0; i < dataSize; i++)
            {
                Assert.Equal(data2to1[i], receivedBy1[i]);
            }

            int offset = 0;
            foreach (var chunk in receivedBy2Chunks)
            {
                for (int i = 0; i < chunk.Length; i++)
                {
                    Assert.Equal(data1to2[offset + i], chunk[i]);
                }
                offset += chunk.Length;
            }
        }
        finally
        {
            kcp1.Dispose();
            kcp2.Dispose();
        }
    }

    [Fact]
    public async Task KcpTransport_BidirectionalStream_2MB()
    {
        const int dataSize = 2 * 1024 * 1024;

        var simulator1to2 = new PerlinNetworkSimulator(seed: 333, baseDropRate: 0.10f, variationAmplitude: 0.08f);
        var simulator2to1 = new PerlinNetworkSimulator(seed: 444, baseDropRate: 0.10f, variationAmplitude: 0.08f);

        using var client1 = new PerlinTestTransport(666666, simulator1to2, simulator2to1);
        using var client2 = new PerlinTestTransport(666666, simulator2to1, simulator1to2);

        client1.SetWindowSize(WindowSize, WindowSize);
        client2.SetWindowSize(WindowSize, WindowSize);
        client1.SetNoDelay(NoDelay, Interval, FastResend, NoCongestion);
        client2.SetNoDelay(NoDelay, Interval, FastResend, NoCongestion);
        client1.SetStreamMode(true);
        client2.SetStreamMode(true);

        client1.OtherTransport = client2;
        client2.OtherTransport = client1;

        client1.Start();
        client2.Start();

        var data1to2 = RandomNumberGenerator.GetBytes(dataSize);
        var data2to1 = RandomNumberGenerator.GetBytes(dataSize);

        var stream1 = client1.GetStream();
        var stream2 = client2.GetStream();

        var buffer1 = new byte[dataSize];
        var buffer2 = new byte[dataSize];

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        var writeTask1 = stream1.WriteAsync(data1to2, cts.Token).AsTask();
        var writeTask2 = stream2.WriteAsync(data2to1, cts.Token).AsTask();
        var readTask1 = stream2.ReadExactlyAsync(buffer2, cts.Token).AsTask();
        var readTask2 = stream1.ReadExactlyAsync(buffer1, cts.Token).AsTask();

        await Task.WhenAll(writeTask1, writeTask2, readTask1, readTask2);

        _output.WriteLine($"=== Test 6: KcpTransport Bidirectional Stream 2MB ===");
        _output.WriteLine($"Direction 1->2: {simulator1to2.GetStatistics()}");
        _output.WriteLine($"Direction 2->1: {simulator2to1.GetStatistics()}");
        _output.WriteLine($"Total packets: {simulator1to2.TotalPackets + simulator2to1.TotalPackets}");
        _output.WriteLine($"Total dropped: {simulator1to2.DroppedPackets + simulator2to1.DroppedPackets}");
        _output.WriteLine($"Overall drop rate: {(simulator1to2.TotalPackets + simulator2to1.TotalPackets > 0 ? (double)(simulator1to2.DroppedPackets + simulator2to1.DroppedPackets) / (simulator1to2.TotalPackets + simulator2to1.TotalPackets) * 100 : 0):F2}%");

        Assert.Equal(data1to2, buffer2);
        Assert.Equal(data2to1, buffer1);

        await client1.StopAsync();
        await client2.StopAsync();
    }

    private static class KcpTestContext
    {
        public static List<byte[]>? Current1;
        public static List<byte[]>? Current2;
    }

    private class PerlinNoise
    {
        private readonly float[] _gradients;
        private readonly int[] _permutation;
        private readonly int _period;

        public PerlinNoise(int seed = 0)
        {
            var random = new Random(seed);
            _period = 256;
            _gradients = new float[_period * 2];
            _permutation = new int[_period * 2];

            for (int i = 0; i < _period; i++)
            {
                _gradients[i] = (float)(random.NextDouble() * 2 - 1);
                _permutation[i] = i;
            }

            for (int i = _period - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (_permutation[i], _permutation[j]) = (_permutation[j], _permutation[i]);
            }

            for (int i = 0; i < _period; i++)
            {
                _permutation[i + _period] = _permutation[i];
                _gradients[i + _period] = _gradients[i];
            }
        }

        public float Noise(float x)
        {
            int xi = (int)Math.Floor(x) & (_period - 1);
            float xf = x - (float)Math.Floor(x);

            float u = Fade(xf);
            float v = 1 - u;

            int hash0 = _permutation[xi];
            int hash1 = _permutation[xi + 1];

            float g0 = _gradients[hash0];
            float g1 = _gradients[hash1];

            float n0 = g0 * xf;
            float n1 = g1 * (xf - 1);

            return Lerp(n0, n1, u);
        }

        public float GetQuality(float time, float frequency = 0.1f)
        {
            float noise = 0;
            float amplitude = 1;
            float totalAmplitude = 0;
            float f = frequency;

            for (int i = 0; i < 4; i++)
            {
                noise += Noise(time * f) * amplitude;
                totalAmplitude += amplitude;
                amplitude *= 0.5f;
                f *= 2;
            }

            noise /= totalAmplitude;

            return (noise + 1) * 0.5f;
        }

        private static float Fade(float t) => t * t * t * (t * (t * 6 - 15) + 10);
        private static float Lerp(float a, float b, float t) => a + t * (b - a);
    }

    private class PerlinNetworkSimulator
    {
        private readonly PerlinNoise _noise;
        private readonly float _baseDropRate;
        private readonly float _variationAmplitude;
        private readonly float _frequency;
        private uint _time;

        public int TotalPackets { get; private set; }
        public int DroppedPackets { get; private set; }
        public int DeliveredPackets => TotalPackets - DroppedPackets;
        public double DropPercentage => TotalPackets > 0 ? (double)DroppedPackets / TotalPackets * 100 : 0;

        public PerlinNetworkSimulator(int seed, float baseDropRate = 0.15f, float variationAmplitude = 0.15f, float frequency = 0.1f)
        {
            _noise = new PerlinNoise(seed);
            _baseDropRate = baseDropRate;
            _variationAmplitude = variationAmplitude;
            _frequency = frequency;
            _time = 0;
        }

        public void AdvanceTime(uint delta = 1)
        {
            _time += delta;
        }

        public bool ShouldDropPacket()
        {
            TotalPackets++;

            float noiseValue = _noise.Noise(_time * _frequency);

            float currentDropRate = _baseDropRate + noiseValue * _variationAmplitude;
            currentDropRate = Math.Clamp(currentDropRate, 0, 1);

            double roll = Random.Shared.NextDouble();
            bool shouldDrop = roll < currentDropRate;

            if (shouldDrop)
                DroppedPackets++;

            return shouldDrop;
        }

        public void ResetStatistics()
        {
            TotalPackets = 0;
            DroppedPackets = 0;
        }

        public string GetStatistics()
        {
            return $"Total: {TotalPackets}, Dropped: {DroppedPackets}, Delivered: {DeliveredPackets}, Drop Rate: {DropPercentage:F2}%";
        }

        public uint CurrentTime => _time;
    }

    private class PerlinTestTransport : KcpTransport
    {
        public PerlinTestTransport? OtherTransport { get; set; }

        private readonly PerlinNetworkSimulator _sendSimulator;
        private readonly PerlinNetworkSimulator _receiveSimulator;

        public Channel<byte[]> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();

        private readonly CancellationTokenSource _cts = new();
        private readonly Task _receiveTask;

        public PerlinTestTransport(uint conv, PerlinNetworkSimulator sendSimulator, PerlinNetworkSimulator receiveSimulator) : base(conv)
        {
            _sendSimulator = sendSimulator;
            _receiveSimulator = receiveSimulator;
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
                    _receiveSimulator.AdvanceTime(1);
                }
            }
            catch (Exception) { }
        }

        protected override async ValueTask<int> SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            _sendSimulator.AdvanceTime(1);

            if (_sendSimulator.ShouldDropPacket())
                return data.Length;

            if (OtherTransport is not null)
            {
                await OtherTransport.Channel.Writer.WriteAsync(data.ToArray(), ct);
                return data.Length;
            }

            return 0;
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
                        _receiveTask.Wait(TimeSpan.FromSeconds(1));
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
