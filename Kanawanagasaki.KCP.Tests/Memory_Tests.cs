namespace Kanawanagasaki.KCP.Tests;

using System.Diagnostics;


public class Memory_Tests
{
    static Memory_Tests()
    {
        KcpMemoryPool.Install();
    }

    private static long RunLoopbackExchange(long totalBytes, uint sndWnd = 256, uint rcvWnd = 256, bool streamMode = false)
    {
        var sender = new KcpManaged(1);
        var receiver = new KcpManaged(1);

        sender.SetWindowSize((int)sndWnd, (int)rcvWnd);
        receiver.SetWindowSize((int)sndWnd, (int)rcvWnd);
        sender.SetNoDelay(true, 10, 2, true);
        receiver.SetNoDelay(true, 10, 2, true);

        if (streamMode)
        {
            sender.IsStreamMode = true;
            receiver.IsStreamMode = true;
        }

        var senderOutput = new List<byte[]>();
        var receiverOutput = new List<byte[]>();

        sender.OnOutput = (data) =>
        {
            senderOutput.Add(data.ToArray());
            return data.Length;
        };
        receiver.OnOutput = (data) =>
        {
            receiverOutput.Add(data.ToArray());
            return data.Length;
        };

        uint time = 1000;
        sender.Update(time);
        receiver.Update(time);

        long bytesSent = 0;
        long bytesReceived = 0;
        int mss = (int)sender.MaximumSegmentSize;

        var sendData = new byte[mss];
        for (int i = 0; i < sendData.Length; i++)
            sendData[i] = (byte)(i & 0xFF);

        var recvBuffer = new byte[mss * 2];

        while (bytesSent < totalBytes || bytesReceived < totalBytes)
        {
            if (bytesSent < totalBytes)
            {
                int toSend = (int)Math.Min(mss, totalBytes - bytesSent);
                var sendResult = sender.Send(sendData.AsSpan(0, toSend));
                if (0 < sendResult)
                    bytesSent += sendResult;
            }

            time += 10;
            sender.Update(time);
            receiver.Update(time);

            foreach (var packet in senderOutput)
                receiver.Input(packet);
            senderOutput.Clear();

            foreach (var packet in receiverOutput)
                sender.Input(packet);
            receiverOutput.Clear();

            while (true)
            {
                int peekSize = receiver.PeekSize();
                if (peekSize <= 0)
                    break;
                int recvResult = receiver.Receive(recvBuffer);
                if (0 < recvResult)
                    bytesReceived += recvResult;
                else
                    break;
            }

            if (totalBytes <= bytesSent && sender.WaitSend() == 0 && totalBytes <= bytesReceived)
                break;
        }

        sender.Dispose();
        receiver.Dispose();

        return bytesReceived;
    }

    [Fact]
    public void MemoryPool_BasicAllocFree()
    {
        KcpMemoryPool.Drain();

        var kcp = new KcpManaged(42);
        kcp.SetWindowSize(64, 64);
        kcp.Update(1000);

        var data = new byte[100];
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)i;

        kcp.Send(data);
        kcp.Flush();

        Assert.True(0 < KcpMemoryPool.InUseCount,
            "Pool should track outstanding allocations");

        long inUseBefore = KcpMemoryPool.InUseCount;

        kcp.Dispose();

        Assert.True(KcpMemoryPool.InUseCount < inUseBefore,
            $"In-use count should decrease after disposal. Before: {inUseBefore}, After: {KcpMemoryPool.InUseCount}");
    }

    [Fact]
    public void MemoryPool_RecyclesSegments()
    {
        KcpMemoryPool.Drain();

        for (int i = 0; i < 10; i++)
        {
            var kcp = new KcpManaged((uint)(1000 + i));
            kcp.SetWindowSize(64, 64);
            kcp.Update(1000);
            var data = new byte[500];
            kcp.Send(data);
            kcp.Flush();
            kcp.Dispose();
        }

        long hits = KcpMemoryPool.PoolHits;
        Assert.True(0 < hits,
            $"Pool should have satisfied some allocations from recycling. Hits: {hits}");
    }

    [Fact]
    public void Transfer_100MB_MessageMode_MemoryBounded()
    {
        KcpMemoryPool.Drain();

        const long totalBytes = 100 * 1024 * 1024;

        var memBefore = Process.GetCurrentProcess().WorkingSet64;
        long received = RunLoopbackExchange(totalBytes, sndWnd: 512, rcvWnd: 512, streamMode: false);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var memAfter = Process.GetCurrentProcess().WorkingSet64;

        Assert.True(totalBytes <= received, $"Should receive all data: {received}/{totalBytes}");

        long deltaMB = (memAfter - memBefore) / (1024 * 1024);
        Assert.True(deltaMB < 200,
            $"Memory grew by {deltaMB}MB after 100MB transfer — expected bounded growth");
    }

    [Fact]
    public void Transfer_100MB_StreamMode_MemoryBounded()
    {
        KcpMemoryPool.Drain();

        const long totalBytes = 100 * 1024 * 1024;

        var memBefore = Process.GetCurrentProcess().WorkingSet64;
        long received = RunLoopbackExchange(totalBytes, sndWnd: 512, rcvWnd: 512, streamMode: true);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var memAfter = Process.GetCurrentProcess().WorkingSet64;

        Assert.True(totalBytes <= received, $"Should receive all data: {received}/{totalBytes}");

        long deltaMB = (memAfter - memBefore) / (1024 * 1024);
        Assert.True(deltaMB < 200,
            $"Memory grew by {deltaMB}MB after 100MB stream transfer — expected bounded growth");
    }

    [Fact]
    public void Transfer_10MB_MessageMode_WorksCorrectly()
    {
        KcpMemoryPool.Drain();
        const long totalBytes = 10 * 1024 * 1024;
        long received = RunLoopbackExchange(totalBytes, sndWnd: 256, rcvWnd: 256, streamMode: false);
        Assert.True(received >= totalBytes, $"Should receive all data: {received}/{totalBytes}");
    }

    [Fact]
    public void Transfer_10MB_StreamMode_WorksCorrectly()
    {
        KcpMemoryPool.Drain();
        const long totalBytes = 10 * 1024 * 1024;
        long received = RunLoopbackExchange(totalBytes, sndWnd: 256, rcvWnd: 256, streamMode: true);
        Assert.True(received >= totalBytes, $"Should receive all data: {received}/{totalBytes}");
    }

    [Fact]
    public void Pool_Stats_Accurate()
    {
        KcpMemoryPool.Drain();

        var kcp = new KcpManaged(9999);
        kcp.SetWindowSize(32, 32);
        kcp.Update(1000);

        var data = new byte[100];
        for (int i = 0; i < 50; i++)
            kcp.Send(data);

        kcp.Flush();

        long outstandingBefore = KcpMemoryPool.InUseCount;
        Assert.True(0 < outstandingBefore, "Should have outstanding allocations after sending data");

        kcp.Dispose();

        long outstandingAfter = KcpMemoryPool.InUseCount;
        Assert.True(outstandingAfter < outstandingBefore,
            $"Outstanding allocations should decrease after disposal. Before: {outstandingBefore}, After: {outstandingAfter}");
    }

    [Fact]
    public void RepeatedCreateDestroy_DoesNotLeak()
    {
        KcpMemoryPool.Drain();

        var memBefore = Process.GetCurrentProcess().WorkingSet64;

        for (int i = 0; i < 100; i++)
        {
            var kcp = new KcpManaged((uint)(5000 + i));
            kcp.SetWindowSize(64, 64);
            kcp.SetNoDelay(true, 10, 2, true);
            kcp.Update(1000);

            var data = new byte[500];
            kcp.Send(data);
            kcp.Flush();
            kcp.Dispose();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var memAfter = Process.GetCurrentProcess().WorkingSet64;
        long deltaMB = (memAfter - memBefore) / (1024 * 1024);

        Assert.True(deltaMB < 50,
            $"Memory grew by {deltaMB}MB after 100 create/destroy cycles — possible leak");
    }
}
