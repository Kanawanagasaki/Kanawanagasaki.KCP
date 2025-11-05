namespace Kanawanagasaki.KCP.Tests;

using System.Buffers.Binary;
using System.Security.Cryptography;

public class Kcp_Tests
{
    [Fact]
    public void GetConvFromBytes()
    {
        var header = new byte[24];
        BinaryPrimitives.WriteUInt32BigEndian(header, 12345u);

        var conv = KcpConversation.GetConvFromBytes(header);

        Assert.Equal(12345u, conv);
    }

    [Fact]
    public void BasicSendRecvCycle()
    {
        var senderOutputData = new List<byte[]>();
        var sender = new KcpConversation(12345);
        sender.SetOutput((data, kcpPtr, userPtr) =>
        {
            senderOutputData.Add(data.ToArray());
            return data.Length;
        });

        var receiverOutputData = new List<byte[]>();
        var receiver = new KcpConversation(12345);
        receiver.SetOutput((data, kcpPtr, userPtr) =>
        {
            receiverOutputData.Add(data.ToArray());
            return data.Length;
        });

        var sendData = new byte[] { 1, 2, 3, 4, 5 };
        var recvData = new byte[100];

        sender.Update(1000);
        var sendResult = sender.Send(sendData);
        sender.Flush();

        Assert.True(0 < sendResult);
        Assert.NotEmpty(senderOutputData);

        receiver.Update(1000);
        foreach (var packet in senderOutputData)
        {
            var inputResult = receiver.Input(packet);
            Assert.Equal(0, inputResult);
        }
        senderOutputData.Clear();

        receiver.Update(2000);
        var recvResult = receiver.Recv(recvData);

        Assert.True(0 < recvResult);

        var received = recvData.Take(recvResult).ToArray();
        Assert.Equal(sendData, received);

        sender.Dispose();
        receiver.Dispose();
    }

    [Fact]
    public void MultiplePackets()
    {
        uint time = 1000;

        var kcp1 = new KcpConversation(12345);
        kcp1.SetWndSize(256, 256);
        var kcp2 = new KcpConversation(12345);
        kcp2.SetWndSize(256, 256);

        kcp1.SetOutput((data, kcpPtr, userPtr) =>
        {
            kcp2.Input(data);
            return data.Length;
        });
        kcp2.SetOutput((data, kcpPtr, userPtr) =>
        {
            kcp1.Input(data);
            return data.Length;
        });

        var packets = new List<byte[]>();
        for (int i = 0; i < byte.MaxValue; i++)
            packets.Add([(byte)(i + 1)]);

        foreach (var packet in packets)
        {
            Assert.True(0 <= kcp1.Send(packet));

            kcp1.Update(time += 1000);
            kcp2.Update(time += 1000);
        }

        kcp1.Flush();
        kcp2.Flush();

        var received = new List<byte[]>();
        var buffer = new byte[10];
        while (true)
        {
            var result = kcp2.Recv(buffer);
            kcp2.Update(time += 1000);
            if (result < 0)
                break;
            if (0 < result)
                received.Add(buffer[..result]);
        }

        Assert.Equal(packets, received);

        kcp1.Dispose();
        kcp2.Dispose();
    }

    [Theory]
    [InlineData(1.0, 0.0)]
    [InlineData(0.5, 0.0)]
    [InlineData(0.5, 0.5)]
    [InlineData(0.25, 0.5)]
    public void MultiplePackets_RandomChance(double deliveryChance, double shuffleChance)
    {
        uint time = 1000;

        var kcp1 = new KcpConversation(12345);
        kcp1.SetWndSize(256, 256);
        var kcp2 = new KcpConversation(12345);
        kcp2.SetWndSize(256, 256);

        var shuffledPackets1 = new List<byte[]>();
        kcp1.SetOutput((data, kcpPtr, userPtr) =>
        {
            if (Random.Shared.NextDouble() <= deliveryChance)
                kcp2.Input(data);
            else if (Random.Shared.NextDouble() <= shuffleChance)
            {
                shuffledPackets1.Add(data.ToArray());
                shuffledPackets1 = shuffledPackets1.OrderBy(_ => Random.Shared.NextDouble()).ToList();
                while (1024 < shuffledPackets1.Count)
                    shuffledPackets1.RemoveAt(0);
            }
            else if (0 < shuffledPackets1.Count)
            {
                kcp2.Input(shuffledPackets1[0]);
                shuffledPackets1.RemoveAt(0);
            }
            return data.Length;
        });

        var shuffledPackets2 = new List<byte[]>();
        kcp2.SetOutput((data, kcpPtr, userPtr) =>
        {
            if (Random.Shared.NextDouble() <= deliveryChance)
                kcp1.Input(data);
            else if (Random.Shared.NextDouble() <= shuffleChance)
            {
                shuffledPackets2.Add(data.ToArray());
                shuffledPackets2 = shuffledPackets2.OrderBy(_ => Random.Shared.NextDouble()).ToList();
                while (1024 < shuffledPackets2.Count)
                    shuffledPackets2.RemoveAt(0);
            }
            else if (0 < shuffledPackets2.Count)
            {
                kcp1.Input(shuffledPackets2[0]);
                shuffledPackets2.RemoveAt(0);
            }
            return data.Length;
        });

        var packets = new List<byte[]>();
        for (int i = 0; i < 256; i++)
            packets.Add(RandomNumberGenerator.GetBytes(8));

        foreach (var packet in packets)
        {
            Assert.True(0 <= kcp1.Send(packet));

            kcp1.Update(time += 1000);
            kcp2.Update(time += 1000);
        }

        kcp1.Flush();
        kcp2.Flush();

        var received = new List<byte[]>();
        var buffer = new byte[10];
        for (int i = 0; i < 100_000_000 && received.Count < packets.Count; i++)
        {
            var result = kcp2.Recv(buffer);
            kcp1.Update(time += 1000);
            kcp2.Update(time += 1000);
            if (0 < result)
                received.Add(buffer[..result]);
        }

        Assert.Equal(packets, received);

        kcp1.Dispose();
        kcp2.Dispose();
    }

    public static int CompareByteArrayLex(byte[] a, byte[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
            if (a[i] != b[i])
                return a[i].CompareTo(b[i]);
        return a.Length.CompareTo(b.Length);
    }
}
