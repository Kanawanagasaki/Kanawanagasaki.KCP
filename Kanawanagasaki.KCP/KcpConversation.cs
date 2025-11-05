namespace Kanawanagasaki.KCP;

using System.Runtime.InteropServices;

public unsafe class KcpConversation : IDisposable
{
    public delegate int KCPOutputDelegate(ReadOnlySpan<byte> buf, IntPtr kcp, IntPtr user);
    public delegate void KCPWriteLogDelegate(string log, IntPtr kcp, IntPtr user);

    private IKCPcb* kcp;
    private bool disposed = false;

    private KCPOutputDelegate? outputDelegate;
    private KCPWriteLogDelegate? writeLogDelegate;

    public uint Conv => kcp->conv;
    public uint Mtu => kcp->mtu;
    public uint State => kcp->state;
    public uint Current => kcp->current;
    public uint Interval => kcp->interval;
    public uint NoDelay => kcp->nodelay;
    public int StreamMode => kcp->stream;
    public uint SendWindow => kcp->snd_wnd;
    public uint ReceiveWindow => kcp->rcv_wnd;
    public uint RemoteWindow => kcp->rmt_wnd;

    public KcpConversation(uint conv)
    {
        kcp = (IKCPcb*)NativeMemory.Alloc((nuint)sizeof(IKCPcb));
        Initialize(conv);
    }

    ~KcpConversation()
    {
        Dispose(false);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposed)
        {
            if (kcp != null)
            {
                Cleanup();
                NativeMemory.Free(kcp);
                kcp = null;
            }

            disposed = true;
        }
    }

    private void Initialize(uint conv)
    {
        NativeMemory.Clear(kcp, (nuint)sizeof(IKCPcb));

        kcp->conv = conv;
        kcp->snd_una = 0;
        kcp->snd_nxt = 0;
        kcp->rcv_nxt = 0;
        kcp->ts_recent = 0;
        kcp->ts_lastack = 0;
        kcp->ts_probe = 0;
        kcp->probe_wait = 0;
        kcp->snd_wnd = KcpConstants.IKCP_WND_SND;
        kcp->rcv_wnd = KcpConstants.IKCP_WND_RCV;
        kcp->rmt_wnd = KcpConstants.IKCP_WND_RCV;
        kcp->cwnd = 0;
        kcp->incr = 0;
        kcp->probe = 0;
        kcp->mtu = KcpConstants.IKCP_MTU_DEF;
        kcp->mss = kcp->mtu - KcpConstants.IKCP_OVERHEAD;
        kcp->stream = 0;
        kcp->state = 0;
        kcp->acklist = null;
        kcp->ackblock = 0;
        kcp->ackcount = 0;
        kcp->rx_srtt = 0;
        kcp->rx_rttval = 0;
        kcp->rx_rto = (int)KcpConstants.IKCP_RTO_DEF;
        kcp->rx_minrto = (int)KcpConstants.IKCP_RTO_MIN;
        kcp->current = 0;
        kcp->interval = KcpConstants.IKCP_INTERVAL;
        kcp->ts_flush = KcpConstants.IKCP_INTERVAL;
        kcp->nodelay = 0;
        kcp->updated = 0;
        kcp->logmask = 0;
        kcp->ssthresh = KcpConstants.IKCP_THRESH_INIT;
        kcp->fastresend = 0;
        kcp->fastlimit = (int)KcpConstants.IKCP_FASTACK_LIMIT;
        kcp->nocwnd = 0;
        kcp->xmit = 0;
        kcp->dead_link = KcpConstants.IKCP_DEADLINK;
        kcp->output_func = null;
        kcp->writelog_func = null;

        InitializeQueue(&kcp->snd_queue);
        InitializeQueue(&kcp->rcv_queue);
        InitializeQueue(&kcp->snd_buf);
        InitializeQueue(&kcp->rcv_buf);

        kcp->nrcv_buf = 0;
        kcp->nsnd_buf = 0;
        kcp->nrcv_que = 0;
        kcp->nsnd_que = 0;

        kcp->buffer = AllocateBuffer((int)(kcp->mtu + KcpConstants.IKCP_OVERHEAD) * 3);
    }

    private static void InitializeQueue(IQueueHead* head)
    {
        head->next = head;
        head->prev = head;
    }

    private static void QueueAdd(IQueueHead* node, IQueueHead* head)
    {
        node->prev = head;
        node->next = head->next;
        head->next->prev = node;
        head->next = node;
    }

    private static void QueueAddTail(IQueueHead* node, IQueueHead* head)
    {
        node->prev = head->prev;
        node->next = head;
        head->prev->next = node;
        head->prev = node;
    }

    private static void QueueDel(IQueueHead* entry)
    {
        entry->next->prev = entry->prev;
        entry->prev->next = entry->next;
        entry->next = null;
        entry->prev = null;
    }

    private static bool QueueIsEmpty(IQueueHead* entry)
    {
        return entry == entry->next;
    }

    private byte* AllocateBuffer(int size)
    {
        return (byte*)NativeMemory.Alloc((nuint)size);
    }

    private void FreeBuffer(byte* buffer)
    {
        if (buffer != null)
        {
            NativeMemory.Free(buffer);
        }
    }

    private IKCPSeg* AllocateSegment(int size)
    {
        int totalSize = sizeof(IKCPSeg) + size - 1;
        return (IKCPSeg*)NativeMemory.Alloc((nuint)totalSize);
    }

    private void FreeSegment(IKCPSeg* seg)
    {
        if (seg != null)
            NativeMemory.Free(seg);
    }

    private void FreeAckList()
    {
        if (kcp->acklist != null)
        {
            NativeMemory.Free(kcp->acklist);
            kcp->acklist = null;
        }
    }

    private void Cleanup()
    {
        if (kcp == null) return;

        CleanupQueue(&kcp->snd_buf);
        CleanupQueue(&kcp->rcv_buf);
        CleanupQueue(&kcp->snd_queue);
        CleanupQueue(&kcp->rcv_queue);

        if (kcp->buffer != null)
        {
            FreeBuffer(kcp->buffer);
            kcp->buffer = null;
        }

        FreeAckList();

        kcp->nrcv_buf = 0;
        kcp->nsnd_buf = 0;
        kcp->nrcv_que = 0;
        kcp->nsnd_que = 0;
    }

    private void CleanupQueue(IQueueHead* head)
    {
        while (!QueueIsEmpty(head))
        {
            IKCPSeg* seg = GetSegmentFromQueueNode(head->next);
            QueueDel(head->next);
            FreeSegment(seg);

            if (head == &kcp->snd_buf) kcp->nsnd_buf--;
            else if (head == &kcp->rcv_buf) kcp->nrcv_buf--;
            else if (head == &kcp->snd_queue) kcp->nsnd_que--;
            else if (head == &kcp->rcv_queue) kcp->nrcv_que--;
        }
    }

    private static IKCPSeg* GetSegmentFromQueueNode(IQueueHead* node)
    {
        return (IKCPSeg*)node;
    }

    public void SetOutput(KCPOutputDelegate output)
    {
        outputDelegate = output;
        GC.KeepAlive(outputDelegate);
    }

    public void SetWriteLog(KCPWriteLogDelegate writelog)
    {
        writeLogDelegate = writelog;
        GC.KeepAlive(writeLogDelegate);
    }

    public int Send(ReadOnlySpan<byte> buffer)
    {
        return SendInternal(buffer);
    }

    public int Recv(Span<byte> buffer)
    {
        return RecvInternal(buffer);
    }

    public void Update(uint current)
    {
        UpdateInternal(current);
    }

    public uint Check(uint current)
    {
        return CheckInternal(current);
    }

    public int Input(ReadOnlySpan<byte> data)
    {
        return InputInternal(data);
    }

    public void Flush()
    {
        FlushInternal();
    }

    public int PeekSize()
    {
        return PeekSizeInternal();
    }

    public int SetMtu(int mtu)
    {
        return SetMtuInternal(mtu);
    }

    public int SetWndSize(int sndwnd, int rcvwnd)
    {
        return SetWndSizeInternal(sndwnd, rcvwnd);
    }

    public int WaitSnd()
    {
        return (int)(kcp->nsnd_buf + kcp->nsnd_que);
    }

    public int SetNoDelay(int nodelay, int interval, int resend, int nc)
    {
        return NoDelayInternal(nodelay, interval, resend, nc);
    }

    public void Log(int mask, string fmt, params object[] args)
    {
        LogInternal(mask, fmt, args);
    }

    public void SetStreamMode(bool enable)
    {
        kcp->stream = enable ? 1 : 0;
    }

    public int SetInterval(int interval)
    {
        return IntervalInternal(interval);
    }

    private int IntervalInternal(int interval)
    {
        if (interval > 5000) interval = 5000;
        else if (interval < 10) interval = 10;
        kcp->interval = (uint)interval;
        return 0;
    }

    public uint GetConv()
    {
        return kcp->conv;
    }

    public static uint GetConvFromBytes(ReadOnlySpan<byte> ptr)
    {
        uint conv;
        Decode32u(ptr, &conv);
        return conv;
    }

    private int SendInternal(ReadOnlySpan<byte> buffer)
    {
        if (kcp->mss <= 0)
            return -1;

        int sent = 0;
        int len = buffer.Length;

        if (kcp->stream != 0)
        {
            if (!QueueIsEmpty(&kcp->snd_queue))
            {
                IQueueHead* lastNode = kcp->snd_queue.prev;
                IKCPSeg* old = GetSegmentFromQueueNode(lastNode);

                if (old->len < kcp->mss)
                {
                    int capacity = (int)(kcp->mss - old->len);
                    int extend = len < capacity ? len : capacity;

                    IKCPSeg* seg = AllocateSegment((int)(old->len + extend));
                    seg->len = old->len + (uint)extend;

                    CopyMemory(seg->data, old->data, (int)old->len);

                    buffer.Slice(0, extend).CopyTo(new Span<byte>(seg->data + old->len, extend));
                    InitializeQueue(&seg->node);

                    seg->conv = kcp->conv;
                    seg->cmd = KcpConstants.IKCP_CMD_PUSH;
                    seg->frg = 0;
                    seg->wnd = old->wnd;
                    seg->ts = kcp->current;
                    seg->sn = old->sn;
                    seg->una = kcp->rcv_nxt;
                    seg->resendts = 0;
                    seg->rto = 0;
                    seg->fastack = 0;
                    seg->xmit = 0;

                    QueueAddTail(&seg->node, &kcp->snd_queue);
                    kcp->nsnd_que++;

                    QueueDel(&old->node);
                    FreeSegment(old);

                    sent = extend;
                    buffer = buffer.Slice(extend);
                    len -= extend;
                }
            }

            if (len <= 0)
                return sent;
        }

        int count = (len <= (int)kcp->mss) ? 1 : (len + (int)kcp->mss - 1) / (int)kcp->mss;

        if ((int)KcpConstants.IKCP_WND_RCV < count)
        {
            if (kcp->stream != 0 && 0 < sent)
                return sent;
            return -2;
        }

        if (count == 0)
            count = 1;

        return sent + SendFragments(buffer, count);
    }

    private int SendFragments(ReadOnlySpan<byte> buffer, int count)
    {
        if (count >= (int)KcpConstants.IKCP_WND_RCV)
            return -2;
        if (count == 0) count = 1;

        int sent = 0;
        var remainingBuffer = buffer;

        for (int i = 0; i < count; i++)
        {
            int size = remainingBuffer.Length > (int)kcp->mss ? (int)kcp->mss : remainingBuffer.Length;

            IKCPSeg* seg = AllocateSegment(size);

            InitializeQueue(&seg->node);

            seg->conv = kcp->conv;
            seg->cmd = KcpConstants.IKCP_CMD_PUSH;
            seg->frg = (kcp->stream == 0) ? (uint)(count - i - 1) : 0;
            seg->wnd = 0;
            seg->ts = 0;
            seg->sn = 0;
            seg->una = 0;
            seg->len = (uint)size;
            seg->resendts = 0;
            seg->rto = 0;
            seg->fastack = 0;
            seg->xmit = 0;

            remainingBuffer.Slice(0, size).CopyTo(new Span<byte>(seg->data, size));

            QueueAddTail(&seg->node, &kcp->snd_queue);
            kcp->nsnd_que++;

            sent += size;
            remainingBuffer = remainingBuffer.Slice(size);
        }

        return sent;
    }

    private int RecvInternal(Span<byte> buffer)
    {
        if (QueueIsEmpty(&kcp->rcv_queue)) return -1;

        bool ispeek = buffer.Length < 0;
        int len = buffer.Length;
        if (len < 0) len = -len;

        int peeksize = PeekSizeInternal();
        if (peeksize < 0) return -2;
        if (peeksize > len) return -3;

        int receiveLen = 0;
        int offset = 0;
        IQueueHead* p = kcp->rcv_queue.next;

        while (p != &kcp->rcv_queue)
        {
            IKCPSeg* seg = GetSegmentFromQueueNode(p);
            p = p->next;

            new ReadOnlySpan<byte>(seg->data, (int)seg->len).CopyTo(buffer.Slice(offset));

            receiveLen += (int)seg->len;
            offset += (int)seg->len;
            uint fragment = seg->frg;

            if (!ispeek)
            {
                QueueDel(&seg->node);
                FreeSegment(seg);
                kcp->nrcv_que--;
            }

            if (fragment == 0)
                break;
        }

        while (!QueueIsEmpty(&kcp->rcv_buf))
        {
            IKCPSeg* seg = GetSegmentFromQueueNode(kcp->rcv_buf.next);
            if (seg->sn == kcp->rcv_nxt && kcp->nrcv_que < kcp->rcv_wnd)
            {
                QueueDel(&seg->node);
                kcp->nrcv_buf--;
                QueueAddTail(&seg->node, &kcp->rcv_queue);
                kcp->nrcv_que++;
                kcp->rcv_nxt++;
            }
            else
                break;
        }

        return receiveLen;
    }

    private void UpdateInternal(uint current)
    {
        kcp->current = current;

        if (kcp->updated == 0)
        {
            kcp->updated = 1;
            kcp->ts_flush = kcp->current;
        }

        int slap = TimeDiff(current, kcp->ts_flush);

        if (slap >= 10000 || slap < -10000)
        {
            kcp->ts_flush = current;
            slap = 0;
        }

        if (slap >= 0)
        {
            kcp->ts_flush += kcp->interval;
            if (TimeDiff(current, kcp->ts_flush) >= 0)
            {
                kcp->ts_flush = current + kcp->interval;
            }
            FlushInternal();
        }
    }

    private uint CheckInternal(uint current)
    {
        uint ts_flush = kcp->ts_flush;
        int tm_flush = 0x7fffffff;
        int tm_packet = 0x7fffffff;

        if (kcp->updated == 0)
            return current;

        if (TimeDiff(current, ts_flush) >= 10000 || TimeDiff(current, ts_flush) < -10000)
            ts_flush = current;

        if (TimeDiff(current, ts_flush) >= 0)
            return current;

        tm_flush = TimeDiff(ts_flush, current);

        for (IQueueHead* p = kcp->snd_buf.next; p != &kcp->snd_buf; p = p->next)
        {
            IKCPSeg* seg = GetSegmentFromQueueNode(p);
            int diff = TimeDiff(seg->resendts, current);
            if (diff <= 0)
                return current;
            if (diff < tm_packet)
                tm_packet = diff;
        }

        uint minimal = (uint)(tm_packet < tm_flush ? tm_packet : tm_flush);
        if (minimal >= kcp->interval)
            minimal = kcp->interval;

        return current + minimal;
    }

    private int InputInternal(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || data.Length < KcpConstants.IKCP_OVERHEAD)
            return -1;

        ReadOnlySpan<byte> remaining = data;
        uint prev_una = kcp->snd_una;
        uint maxack = 0, latest_ts = 0;
        int flag = 0;

        while (remaining.Length >= KcpConstants.IKCP_OVERHEAD)
        {
            uint conv, ts, sn, una, len;
            ushort wnd;
            byte cmd, frg;

            remaining = Decode32u(remaining, &conv);
            if (conv != kcp->conv)
                return -1;

            remaining = Decode8u(remaining, &cmd);
            remaining = Decode8u(remaining, &frg);
            remaining = Decode16u(remaining, &wnd);
            remaining = Decode32u(remaining, &ts);
            remaining = Decode32u(remaining, &sn);
            remaining = Decode32u(remaining, &una);
            remaining = Decode32u(remaining, &len);

            if (remaining.Length < len || len < 0)
                return -2;

            if (cmd != KcpConstants.IKCP_CMD_PUSH &&
                cmd != KcpConstants.IKCP_CMD_ACK &&
                cmd != KcpConstants.IKCP_CMD_WASK &&
                cmd != KcpConstants.IKCP_CMD_WINS)
                return -3;

            kcp->rmt_wnd = wnd;
            ParseUna(una);
            ShrinkBuf();

            if (cmd == KcpConstants.IKCP_CMD_ACK)
            {
                if (TimeDiff(kcp->current, ts) >= 0)
                {
                    UpdateAck(TimeDiff(kcp->current, ts));
                }
                ParseAck(sn);
                ShrinkBuf();
                if (flag == 0)
                {
                    flag = 1;
                    maxack = sn;
                    latest_ts = ts;
                }
                else
                {
                    if (TimeDiff(sn, maxack) > 0)
                    {
                        maxack = sn;
                        latest_ts = ts;
                    }
                }
            }
            else if (cmd == KcpConstants.IKCP_CMD_PUSH)
            {
                if (TimeDiff(sn, kcp->rcv_nxt + kcp->rcv_wnd) < 0)
                {
                    AckPush(sn, ts);
                    if (TimeDiff(sn, kcp->rcv_nxt) >= 0)
                    {
                        IKCPSeg* seg = AllocateSegment((int)len);
                        seg->conv = conv;
                        seg->cmd = cmd;
                        seg->frg = frg;
                        seg->wnd = wnd;
                        seg->ts = ts;
                        seg->sn = sn;
                        seg->una = una;
                        seg->len = len;

                        if (len > 0)
                        {
                            remaining.Slice(0, (int)len).CopyTo(new Span<byte>(seg->data, (int)len));
                        }

                        ParseData(seg);
                    }
                }
            }
            else if (cmd == KcpConstants.IKCP_CMD_WASK)
            {
                kcp->probe |= KcpConstants.IKCP_ASK_TELL;
            }
            else if (cmd == KcpConstants.IKCP_CMD_WINS)
            {
                // Do nothing for window size updates
            }
            else
            {
                return -3;
            }

            remaining = remaining.Slice((int)len);
        }

        if (flag != 0)
        {
            ParseFastAck(maxack, latest_ts);
        }

        if (TimeDiff(kcp->snd_una, prev_una) > 0)
        {
            if (kcp->cwnd < kcp->rmt_wnd)
            {
                uint mss = kcp->mss;
                if (kcp->cwnd < kcp->ssthresh)
                {
                    kcp->cwnd++;
                    kcp->incr += mss;
                }
                else
                {
                    if (kcp->incr < mss) kcp->incr = mss;
                    kcp->incr += (mss * mss) / kcp->incr + (mss / 16);
                    if ((kcp->cwnd + 1) * mss <= kcp->incr)
                    {
                        kcp->cwnd = (kcp->incr + mss - 1) / ((mss > 0) ? mss : 1);
                    }
                }
                if (kcp->cwnd > kcp->rmt_wnd)
                {
                    kcp->cwnd = kcp->rmt_wnd;
                    kcp->incr = kcp->rmt_wnd * mss;
                }
            }
        }

        return 0;
    }

    private void ParseData(IKCPSeg* newseg)
    {
        IQueueHead* p;
        IQueueHead* prev;
        uint sn = newseg->sn;
        int repeat = 0;

        if (TimeDiff(sn, kcp->rcv_nxt + kcp->rcv_wnd) >= 0 ||
            TimeDiff(sn, kcp->rcv_nxt) < 0)
        {
            FreeSegment(newseg);
            return;
        }

        for (p = kcp->rcv_buf.prev; p != &kcp->rcv_buf; p = prev)
        {
            IKCPSeg* seg = GetSegmentFromQueueNode(p);
            prev = p->prev;
            if (seg->sn == sn)
            {
                repeat = 1;
                break;
            }
            if (TimeDiff(sn, seg->sn) > 0)
            {
                break;
            }
        }

        if (repeat == 0)
        {
            InitializeQueue(&newseg->node);
            QueueAdd(&newseg->node, p);
            kcp->nrcv_buf++;
        }
        else
        {
            FreeSegment(newseg);
        }

        while (!QueueIsEmpty(&kcp->rcv_buf))
        {
            IKCPSeg* seg = GetSegmentFromQueueNode(kcp->rcv_buf.next);
            if (seg->sn == kcp->rcv_nxt && kcp->nrcv_que < kcp->rcv_wnd)
            {
                QueueDel(&seg->node);
                kcp->nrcv_buf--;
                QueueAddTail(&seg->node, &kcp->rcv_queue);
                kcp->nrcv_que++;
                kcp->rcv_nxt++;
            }
            else
            {
                break;
            }
        }
    }

    private void FlushInternal()
    {
        uint current = kcp->current;
        byte* buffer = kcp->buffer;
        byte* ptr = buffer;
        int count, size, i;
        uint resent, cwnd;
        uint rtomin;
        IQueueHead* p;
        int change = 0;
        int lost = 0;
        IKCPSeg seg = new IKCPSeg();

        if (kcp->updated == 0) return;

        seg.conv = kcp->conv;
        seg.cmd = KcpConstants.IKCP_CMD_ACK;
        seg.frg = 0;
        seg.wnd = (uint)WindowUnused();
        seg.una = kcp->rcv_nxt;
        seg.len = 0;
        seg.sn = 0;
        seg.ts = 0;

        count = (int)kcp->ackcount;
        for (i = 0; i < count; i++)
        {
            size = (int)(ptr - buffer);
            if (size + (int)KcpConstants.IKCP_OVERHEAD > (int)kcp->mtu)
            {
                Output(buffer, size);
                ptr = buffer;
            }
            GetAck(i, &seg.sn, &seg.ts);
            ptr = EncodeSeg(ptr, &seg);
        }

        kcp->ackcount = 0;

        if (kcp->rmt_wnd == 0)
        {
            if (kcp->probe_wait == 0)
            {
                kcp->probe_wait = KcpConstants.IKCP_PROBE_INIT;
                kcp->ts_probe = kcp->current + kcp->probe_wait;
            }
            else
            {
                if (TimeDiff(kcp->current, kcp->ts_probe) >= 0)
                {
                    if (kcp->probe_wait < KcpConstants.IKCP_PROBE_INIT)
                        kcp->probe_wait = KcpConstants.IKCP_PROBE_INIT;
                    kcp->probe_wait += kcp->probe_wait / 2;
                    if (kcp->probe_wait > KcpConstants.IKCP_PROBE_LIMIT)
                        kcp->probe_wait = KcpConstants.IKCP_PROBE_LIMIT;
                    kcp->ts_probe = kcp->current + kcp->probe_wait;
                    kcp->probe |= KcpConstants.IKCP_ASK_SEND;
                }
            }
        }
        else
        {
            kcp->ts_probe = 0;
            kcp->probe_wait = 0;
        }

        if ((kcp->probe & KcpConstants.IKCP_ASK_SEND) != 0)
        {
            seg.cmd = KcpConstants.IKCP_CMD_WASK;
            size = (int)(ptr - buffer);
            if (size + (int)KcpConstants.IKCP_OVERHEAD > (int)kcp->mtu)
            {
                Output(buffer, size);
                ptr = buffer;
            }
            ptr = EncodeSeg(ptr, &seg);
        }

        if ((kcp->probe & KcpConstants.IKCP_ASK_TELL) != 0)
        {
            seg.cmd = KcpConstants.IKCP_CMD_WINS;
            size = (int)(ptr - buffer);
            if (size + (int)KcpConstants.IKCP_OVERHEAD > (int)kcp->mtu)
            {
                Output(buffer, size);
                ptr = buffer;
            }
            ptr = EncodeSeg(ptr, &seg);
        }

        kcp->probe = 0;

        cwnd = Min(kcp->snd_wnd, kcp->rmt_wnd);
        if (kcp->nocwnd == 0) cwnd = Min(kcp->cwnd, cwnd);

        while (TimeDiff(kcp->snd_nxt, kcp->snd_una + cwnd) < 0)
        {
            if (QueueIsEmpty(&kcp->snd_queue)) break;

            IKCPSeg* newseg = GetSegmentFromQueueNode(kcp->snd_queue.next);

            QueueDel(&newseg->node);
            kcp->nsnd_que--;

            newseg->conv = kcp->conv;
            newseg->cmd = KcpConstants.IKCP_CMD_PUSH;
            newseg->wnd = seg.wnd;
            newseg->ts = current;
            newseg->sn = kcp->snd_nxt++;
            newseg->una = kcp->rcv_nxt;
            newseg->resendts = current;
            newseg->rto = (uint)kcp->rx_rto;
            newseg->fastack = 0;
            newseg->xmit = 0;

            QueueAddTail(&newseg->node, &kcp->snd_buf);
            kcp->nsnd_buf++;
        }

        resent = (kcp->fastresend > 0) ? (uint)kcp->fastresend : 0xffffffff;
        rtomin = (kcp->nodelay == 0) ? (uint)(kcp->rx_rto >> 3) : 0;

        for (p = kcp->snd_buf.next; p != &kcp->snd_buf; p = p->next)
        {
            IKCPSeg* segment = GetSegmentFromQueueNode(p);
            int needsend = 0;
            if (segment->xmit == 0)
            {
                needsend = 1;
                segment->xmit++;
                segment->rto = (uint)kcp->rx_rto;
                segment->resendts = current + segment->rto + rtomin;
            }
            else if (TimeDiff(current, segment->resendts) >= 0)
            {
                needsend = 1;
                segment->xmit++;
                kcp->xmit++;
                if (kcp->nodelay == 0)
                {
                    segment->rto += Max(segment->rto, (uint)kcp->rx_rto);
                }
                else
                {
                    int step = (kcp->nodelay < 2) ? (int)(segment->rto) : kcp->rx_rto;
                    segment->rto += (uint)step / 2;
                }
                segment->resendts = current + segment->rto;
                lost = 1;
            }
            else if (segment->fastack >= resent)
            {
                if ((int)segment->xmit <= kcp->fastlimit || kcp->fastlimit <= 0)
                {
                    needsend = 1;
                    segment->xmit++;
                    segment->fastack = 0;
                    segment->resendts = current + segment->rto;
                    change++;
                }
            }

            if (needsend != 0)
            {
                int need;
                segment->ts = current;
                segment->wnd = seg.wnd;
                segment->una = kcp->rcv_nxt;

                size = (int)(ptr - buffer);
                need = (int)(KcpConstants.IKCP_OVERHEAD + segment->len);

                if (size + need > (int)kcp->mtu)
                {
                    Output(buffer, size);
                    ptr = buffer;
                }

                ptr = EncodeSeg(ptr, segment);

                if (segment->len > 0)
                {
                    CopyMemory(ptr, segment->data, (int)segment->len);
                    ptr += segment->len;
                }

                if (segment->xmit >= kcp->dead_link)
                {
                    kcp->state = uint.MaxValue;
                }
            }
        }

        size = (int)(ptr - buffer);
        if (size > 0)
        {
            Output(buffer, size);
        }

        if (change != 0)
        {
            uint inflight = kcp->snd_nxt - kcp->snd_una;
            kcp->ssthresh = inflight / 2;
            if (kcp->ssthresh < KcpConstants.IKCP_THRESH_MIN)
                kcp->ssthresh = KcpConstants.IKCP_THRESH_MIN;
            kcp->cwnd = kcp->ssthresh + resent;
            kcp->incr = kcp->cwnd * kcp->mss;
        }

        if (lost != 0)
        {
            kcp->ssthresh = cwnd / 2;
            if (kcp->ssthresh < KcpConstants.IKCP_THRESH_MIN)
                kcp->ssthresh = KcpConstants.IKCP_THRESH_MIN;
            kcp->cwnd = 1;
            kcp->incr = kcp->mss;
        }

        if (kcp->cwnd < 1)
        {
            kcp->cwnd = 1;
            kcp->incr = kcp->mss;
        }
    }

    private int PeekSizeInternal()
    {
        if (QueueIsEmpty(&kcp->rcv_queue))
            return -1;

        IKCPSeg* seg = GetSegmentFromQueueNode(kcp->rcv_queue.next);
        if (seg->frg == 0)
            return (int)seg->len;

        if (kcp->nrcv_que < seg->frg + 1)
            return -1;

        int length = 0;
        for (IQueueHead* p = kcp->rcv_queue.next; p != &kcp->rcv_queue; p = p->next)
        {
            seg = GetSegmentFromQueueNode(p);
            length += (int)seg->len;
            if (seg->frg == 0)
                break;
        }

        return length;
    }

    private int SetMtuInternal(int mtu)
    {
        if (mtu < 50 || mtu < (int)KcpConstants.IKCP_OVERHEAD)
            return -1;

        byte* newBuffer = AllocateBuffer((mtu + (int)KcpConstants.IKCP_OVERHEAD) * 3);
        if (newBuffer == null)
            return -2;

        FreeBuffer(kcp->buffer);
        kcp->buffer = newBuffer;
        kcp->mtu = (uint)mtu;
        kcp->mss = kcp->mtu - KcpConstants.IKCP_OVERHEAD;

        return 0;
    }

    private int SetWndSizeInternal(int sndwnd, int rcvwnd)
    {
        if (sndwnd > 0)
            kcp->snd_wnd = (uint)sndwnd;
        if (rcvwnd > 0)
            kcp->rcv_wnd = Max((uint)rcvwnd, KcpConstants.IKCP_WND_RCV);

        return 0;
    }

    private int NoDelayInternal(int nodelay, int interval, int resend, int nc)
    {
        if (nodelay >= 0)
        {
            kcp->nodelay = (uint)nodelay;
            if (nodelay != 0)
                kcp->rx_minrto = (int)KcpConstants.IKCP_RTO_NDL;
            else
                kcp->rx_minrto = (int)KcpConstants.IKCP_RTO_MIN;
        }

        if (interval >= 0)
        {
            if (interval > 5000) interval = 5000;
            else if (interval < 10) interval = 10;
            kcp->interval = (uint)interval;
        }

        if (resend >= 0)
            kcp->fastresend = resend;

        if (nc >= 0)
            kcp->nocwnd = nc;

        return 0;
    }

    private void LogInternal(int mask, string fmt, params object[] args)
    {
        if (writeLogDelegate != null)
        {
            string message = string.Format(fmt, args);
            writeLogDelegate(message, (IntPtr)kcp, IntPtr.Zero);
        }
    }

    private int WindowUnused()
    {
        if (kcp->nrcv_que < kcp->rcv_wnd)
        {
            return (int)(kcp->rcv_wnd - kcp->nrcv_que);
        }
        return 0;
    }

    private void GetAck(int p, uint* sn, uint* ts)
    {
        if (sn != null) *sn = kcp->acklist[p * 2 + 0];
        if (ts != null) *ts = kcp->acklist[p * 2 + 1];
    }

    private byte* EncodeSeg(byte* ptr, IKCPSeg* seg)
    {
        ptr = Encode32u(ptr, seg->conv);
        ptr = Encode8u(ptr, (byte)seg->cmd);
        ptr = Encode8u(ptr, (byte)seg->frg);
        ptr = Encode16u(ptr, (ushort)seg->wnd);
        ptr = Encode32u(ptr, seg->ts);
        ptr = Encode32u(ptr, seg->sn);
        ptr = Encode32u(ptr, seg->una);
        ptr = Encode32u(ptr, seg->len);
        return ptr;
    }

    private void Output(byte* buffer, int size)
    {
        if (size == 0 || outputDelegate == null) return;

        var data = new ReadOnlySpan<byte>(buffer, size);

        try
        {
            outputDelegate(data, (IntPtr)kcp, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            if (writeLogDelegate != null)
            {
                writeLogDelegate($"Output error: {ex.Message}", (IntPtr)kcp, IntPtr.Zero);
            }
        }
    }

    private void AckPush(uint sn, uint ts)
    {
        uint newsize = kcp->ackcount + 1;

        if (newsize > kcp->ackblock)
        {
            uint* acklist;
            uint newblock;

            for (newblock = 8; newblock < newsize; newblock <<= 1) ;
            int size = (int)(newblock * sizeof(uint) * 2);

            acklist = (uint*)NativeMemory.Alloc((nuint)size);

            if (kcp->acklist != null)
            {
                for (uint x = 0; x < kcp->ackcount; x++)
                {
                    acklist[x * 2 + 0] = kcp->acklist[x * 2 + 0];
                    acklist[x * 2 + 1] = kcp->acklist[x * 2 + 1];
                }
                FreeAckList();
            }

            kcp->acklist = acklist;
            kcp->ackblock = newblock;
        }

        uint* ptr = &kcp->acklist[kcp->ackcount * 2];
        ptr[0] = sn;
        ptr[1] = ts;
        kcp->ackcount++;
    }

    private void UpdateAck(int rtt)
    {
        int rto = 0;
        if (kcp->rx_srtt == 0)
        {
            kcp->rx_srtt = rtt;
            kcp->rx_rttval = rtt / 2;
        }
        else
        {
            int delta = rtt - kcp->rx_srtt;
            if (delta < 0) delta = -delta;
            kcp->rx_rttval = (3 * kcp->rx_rttval + delta) / 4;
            kcp->rx_srtt = (7 * kcp->rx_srtt + rtt) / 8;
            if (kcp->rx_srtt < 1) kcp->rx_srtt = 1;
        }
        rto = kcp->rx_srtt + Max((int)kcp->interval, 4 * kcp->rx_rttval);
        kcp->rx_rto = Bound(kcp->rx_minrto, rto, (int)KcpConstants.IKCP_RTO_MAX);
    }

    private void ShrinkBuf()
    {
        IQueueHead* p = kcp->snd_buf.next;
        if (p != &kcp->snd_buf)
        {
            IKCPSeg* seg = GetSegmentFromQueueNode(p);
            kcp->snd_una = seg->sn;
        }
        else
        {
            kcp->snd_una = kcp->snd_nxt;
        }
    }

    private void ParseAck(uint sn)
    {
        IQueueHead* p;
        IQueueHead* next;

        if (TimeDiff(sn, kcp->snd_una) < 0 || TimeDiff(sn, kcp->snd_nxt) >= 0)
            return;

        for (p = kcp->snd_buf.next; p != &kcp->snd_buf; p = next)
        {
            IKCPSeg* seg = GetSegmentFromQueueNode(p);
            next = p->next;
            if (sn == seg->sn)
            {
                QueueDel(p);
                FreeSegment(seg);
                kcp->nsnd_buf--;
                break;
            }
            if (TimeDiff(sn, seg->sn) < 0)
            {
                break;
            }
        }
    }

    private void ParseUna(uint una)
    {
        IQueueHead* p;
        IQueueHead* next;
        for (p = kcp->snd_buf.next; p != &kcp->snd_buf; p = next)
        {
            IKCPSeg* seg = GetSegmentFromQueueNode(p);
            next = p->next;
            if (TimeDiff(una, seg->sn) > 0)
            {
                QueueDel(p);
                FreeSegment(seg);
                kcp->nsnd_buf--;
            }
            else
            {
                break;
            }
        }
    }

    private void ParseFastAck(uint sn, uint ts)
    {
        IQueueHead* p;
        IQueueHead* next;

        if (TimeDiff(sn, kcp->snd_una) < 0 || TimeDiff(sn, kcp->snd_nxt) >= 0)
            return;

        for (p = kcp->snd_buf.next; p != &kcp->snd_buf; p = next)
        {
            IKCPSeg* seg = GetSegmentFromQueueNode(p);
            next = p->next;
            if (TimeDiff(sn, seg->sn) < 0)
            {
                break;
            }
            else if (sn != seg->sn)
            {
                seg->fastack++;
            }
        }
    }

    private static void CopyMemory(byte* dest, byte* src, int length)
    {
        Buffer.MemoryCopy(src, dest, length, length);
    }

    private static void CopyMemory(byte[] dest, int destOffset, byte* src, int srcOffset, int length)
    {
        fixed (byte* d = dest)
        {
            CopyMemory(d + destOffset, src + srcOffset, length);
        }
    }

    private static byte* Encode8u(byte* p, byte c)
    {
        *p++ = c;
        return p;
    }

    private static byte* Decode8u(byte* p, byte* c)
    {
        *c = *p++;
        return p;
    }

    private static ReadOnlySpan<byte> Decode8u(ReadOnlySpan<byte> p, byte* c)
    {
        *c = p[0];
        return p.Slice(1);
    }

    private static byte* Encode16u(byte* p, ushort w)
    {
        p[0] = (byte)(w >> 8);
        p[1] = (byte)(w & 0xFF);
        return p + 2;
    }

    private static byte* Decode16u(byte* p, ushort* w)
    {
        *w = (ushort)((p[0] << 8) | p[1]);
        return p + 2;
    }

    private static ReadOnlySpan<byte> Decode16u(ReadOnlySpan<byte> p, ushort* w)
    {
        *w = (ushort)((p[0] << 8) | p[1]);
        return p.Slice(2);
    }

    private static byte* Encode32u(byte* p, uint l)
    {
        p[0] = (byte)((l >> 24) & 0xff);
        p[1] = (byte)((l >> 16) & 0xff);
        p[2] = (byte)((l >> 8) & 0xff);
        p[3] = (byte)(l & 0xff);
        return p + 4;
    }

    private static byte* Decode32u(byte* p, uint* l)
    {
        uint val = ((uint)p[0] << 24) |
                   ((uint)p[1] << 16) |
                   ((uint)p[2] << 8) |
                   ((uint)p[3]);
        if (l != null) *l = val;
        return p + 4;
    }

    private static ReadOnlySpan<byte> Decode32u(ReadOnlySpan<byte> p, uint* l)
    {
        uint val = ((uint)p[0] << 24) |
                   ((uint)p[1] << 16) |
                   ((uint)p[2] << 8) |
                   ((uint)p[3]);
        if (l != null) *l = val;
        return p.Slice(4);
    }

    private static int TimeDiff(uint later, uint earlier)
    {
        return (int)(later - earlier);
    }

    private static uint Max(uint a, uint b)
    {
        return a >= b ? a : b;
    }

    private static uint Min(uint a, uint b)
    {
        return a <= b ? a : b;
    }

    private static int Min(int a, int b)
    {
        return a <= b ? a : b;
    }

    private static int Max(int a, int b)
    {
        return a >= b ? a : b;
    }

    private static int Bound(int lower, int middle, int upper)
    {
        return Min(Max(lower, middle), upper);
    }
}
