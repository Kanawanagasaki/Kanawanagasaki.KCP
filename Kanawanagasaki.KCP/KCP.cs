namespace Kanawanagasaki.KCP;

using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

public static unsafe class KCP
{
    private static readonly bool IWORDS_BIG_ENDIAN = !BitConverter.IsLittleEndian;

    private static readonly bool IWORDS_MUST_ALIGN =
        !((sizeof(void*) == 8 && RuntimeInformation.ProcessArchitecture == Architecture.X64) ||
          (sizeof(void*) == 4 && RuntimeInformation.ProcessArchitecture == Architecture.X86));

    private static delegate*<nuint, void*> ikcp_malloc_hook = null;
    private static delegate*<void*, void> ikcp_free_hook = null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void iqueue_init(IQueueHead* queue)
    {
        queue->next = queue;
        queue->prev = queue;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void iqueue_add(IQueueHead* node, IQueueHead* head)
    {
        node->prev = head;
        node->next = head->next;
        head->next->prev = node;
        head->next = node;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void iqueue_add_tail(IQueueHead* node, IQueueHead* head)
    {
        node->prev = head->prev;
        node->next = head;
        head->prev->next = node;
        head->prev = node;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void iqueue_del(IQueueHead* entry)
    {
        entry->next->prev = entry->prev;
        entry->prev->next = entry->next;
        entry->next = null;
        entry->prev = null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void iqueue_del_init(IQueueHead* entry)
    {
        iqueue_del(entry);
        iqueue_init(entry);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool iqueue_is_empty(IQueueHead* head)
    {
        return head == head->next;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static IKCPSEG* iqueue_entry(IQueueHead* ptr)
    {
        return (IKCPSEG*)ptr;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte* ikcp_encode8u(byte* p, byte c)
    {
        *p++ = c;
        return p;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte* ikcp_decode8u(byte* p, byte* c)
    {
        *c = *p++;
        return p;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte* ikcp_encode16u(byte* p, ushort w)
    {
        if (IWORDS_BIG_ENDIAN || IWORDS_MUST_ALIGN)
        {
            p[0] = (byte)(w & 255);
            p[1] = (byte)(w >> 8);
        }
        else
        {
            *(ushort*)p = w;
        }
        p += 2;
        return p;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte* ikcp_decode16u(byte* p, ushort* w)
    {
        if (IWORDS_BIG_ENDIAN || IWORDS_MUST_ALIGN)
        {
            *w = (ushort)((p[0]) + ((uint)p[1] << 8));
        }
        else
        {
            *w = *(ushort*)p;
        }
        p += 2;
        return p;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte* ikcp_encode32u(byte* p, uint l)
    {
        if (IWORDS_BIG_ENDIAN || IWORDS_MUST_ALIGN)
        {
            p[0] = (byte)((l >> 0) & 0xff);
            p[1] = (byte)((l >> 8) & 0xff);
            p[2] = (byte)((l >> 16) & 0xff);
            p[3] = (byte)((l >> 24) & 0xff);
        }
        else
        {
            *(uint*)p = l;
        }
        p += 4;
        return p;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte* ikcp_decode32u(byte* p, uint* l)
    {
        if (IWORDS_BIG_ENDIAN || IWORDS_MUST_ALIGN)
        {
            *l = p[3];
            *l = (uint)(p[2] + (*l << 8));
            *l = (uint)(p[1] + (*l << 8));
            *l = (uint)(p[0] + (*l << 8));
        }
        else
        {
            *l = *(uint*)p;
        }
        p += 4;
        return p;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint _imin_(uint a, uint b) => a <= b ? a : b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint _imax_(uint a, uint b) => a >= b ? a : b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint _ibound_(uint lower, uint middle, uint upper)
    {
        return _imin_(_imax_(lower, middle), upper);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int _itimediff(uint later, uint earlier)
    {
        return ((int)(later - earlier));
    }

    public static void ikcp_allocator(delegate*<nuint, void*> new_malloc, delegate*<void*, void> new_free)
    {
        ikcp_malloc_hook = new_malloc;
        ikcp_free_hook = new_free;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void* ikcp_malloc(nuint size)
    {
        if (ikcp_malloc_hook != null)
            return ikcp_malloc_hook(size);
        return NativeMemory.Alloc(size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ikcp_free(void* ptr)
    {
        if (ikcp_free_hook != null)
        {
            ikcp_free_hook(ptr);
        }
        else
        {
            NativeMemory.Free(ptr);
        }
    }

    private static IKCPSEG* ikcp_segment_new(IKCPCB* kcp, int size)
    {
        return (IKCPSEG*)ikcp_malloc((nuint)(sizeof(IKCPSEG) + size));
    }

    private static void ikcp_segment_delete(IKCPCB* kcp, IKCPSEG* seg)
    {
        ikcp_free(seg);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ikcp_canlog(IKCPCB* kcp, int mask)
    {
        if ((mask & kcp->logmask) == 0 || kcp->writelog == null) return 0;
        return 1;
    }

    public static void ikcp_log(IKCPCB* kcp, int mask, string fmt, params object[] args)
    {
        if ((mask & kcp->logmask) == 0 || kcp->writelog == null) return;

        string formatted = string.Format(fmt, args);
        byte* buffer = stackalloc byte[1024];
        int len = System.Text.Encoding.UTF8.GetBytes(formatted, new Span<byte>(buffer, 1023));
        buffer[len] = 0;
        kcp->writelog(buffer, kcp, kcp->user);
    }

    private static int ikcp_output(IKCPCB* kcp, void* data, int size)
    {
        if (kcp->output == null) return 0;
        if (ikcp_canlog(kcp, KcpConstants.IKCP_LOG_OUTPUT) != 0)
        {
            ikcp_log(kcp, KcpConstants.IKCP_LOG_OUTPUT, "[RO] {0} bytes", size);
        }
        if (size == 0) return 0;
        return kcp->output((byte*)data, size, kcp, kcp->user);
    }

    public static IKCPCB* ikcp_create(uint conv, void* user)
    {
        IKCPCB* kcp = (IKCPCB*)ikcp_malloc((nuint)sizeof(IKCPCB));
        if (kcp == null) return null;

        kcp->conv = conv;
        kcp->user = user;
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

        kcp->buffer = (byte*)ikcp_malloc((nuint)((kcp->mtu + KcpConstants.IKCP_OVERHEAD) * 3));
        if (kcp->buffer == null)
        {
            ikcp_free(kcp);
            return null;
        }

        iqueue_init(&kcp->snd_queue);
        iqueue_init(&kcp->rcv_queue);
        iqueue_init(&kcp->snd_buf);
        iqueue_init(&kcp->rcv_buf);

        kcp->nrcv_buf = 0;
        kcp->nsnd_buf = 0;
        kcp->nrcv_que = 0;
        kcp->nsnd_que = 0;
        kcp->state = 0;
        kcp->acklist = null;
        kcp->ackblock = 0;
        kcp->ackcount = 0;
        kcp->ackedlen = 0;
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
        kcp->output = null;
        kcp->ccops = null;
        kcp->congest = null;
        kcp->writelog = null;

        return kcp;
    }

    public static void ikcp_release(IKCPCB* kcp)
    {
        if (kcp == null) return;

        IKCPSEG* seg;

        if (kcp->ccops != null && kcp->ccops->release != null)
        {
            kcp->ccops->release(kcp);
        }

        while (!iqueue_is_empty(&kcp->snd_buf))
        {
            seg = iqueue_entry(kcp->snd_buf.next);
            iqueue_del(&seg->node);
            ikcp_segment_delete(kcp, seg);
        }
        while (!iqueue_is_empty(&kcp->rcv_buf))
        {
            seg = iqueue_entry(kcp->rcv_buf.next);
            iqueue_del(&seg->node);
            ikcp_segment_delete(kcp, seg);
        }
        while (!iqueue_is_empty(&kcp->snd_queue))
        {
            seg = iqueue_entry(kcp->snd_queue.next);
            iqueue_del(&seg->node);
            ikcp_segment_delete(kcp, seg);
        }
        while (!iqueue_is_empty(&kcp->rcv_queue))
        {
            seg = iqueue_entry(kcp->rcv_queue.next);
            iqueue_del(&seg->node);
            ikcp_segment_delete(kcp, seg);
        }

        if (kcp->buffer != null)
        {
            ikcp_free(kcp->buffer);
        }
        if (kcp->acklist != null)
        {
            ikcp_free(kcp->acklist);
        }

        kcp->nrcv_buf = 0;
        kcp->nsnd_buf = 0;
        kcp->nrcv_que = 0;
        kcp->nsnd_que = 0;
        kcp->ackcount = 0;
        kcp->buffer = null;
        kcp->acklist = null;
        ikcp_free(kcp);
    }

    public static void ikcp_setoutput(IKCPCB* kcp, delegate* unmanaged[Cdecl]<byte*, int, IKCPCB*, void*, int> output)
    {
        kcp->output = output;
    }

    public static int ikcp_recv(IKCPCB* kcp, byte* buffer, int len)
    {
        IQueueHead* p;
        int ispeek = (len < 0) ? 1 : 0;
        int peeksize;
        int recover = 0;
        IKCPSEG* seg;

        if (iqueue_is_empty(&kcp->rcv_queue))
            return -1;

        if (len < 0) len = -len;

        peeksize = ikcp_peeksize(kcp);

        if (peeksize < 0)
            return -2;

        if (peeksize > len)
            return -3;

        if (kcp->nrcv_que >= kcp->rcv_wnd)
            recover = 1;

        for (len = 0, p = kcp->rcv_queue.next; p != &kcp->rcv_queue;)
        {
            int fragment;
            seg = iqueue_entry(p);
            p = p->next;

            if (buffer != null)
            {
                Buffer.MemoryCopy(((byte*)seg) + sizeof(IKCPSEG), buffer, seg->len, seg->len);
                buffer += (int)seg->len;
            }

            len += (int)seg->len;
            fragment = (int)seg->frg;

            if (ikcp_canlog(kcp, KcpConstants.IKCP_LOG_RECV) != 0)
            {
                ikcp_log(kcp, KcpConstants.IKCP_LOG_RECV, "recv sn={0}", seg->sn);
            }

            if (ispeek == 0)
            {
                iqueue_del(&seg->node);
                ikcp_segment_delete(kcp, seg);
                kcp->nrcv_que--;
            }

            if (fragment == 0)
                break;
        }

        while (!iqueue_is_empty(&kcp->rcv_buf))
        {
            seg = iqueue_entry(kcp->rcv_buf.next);
            if (seg->sn == kcp->rcv_nxt && kcp->nrcv_que < kcp->rcv_wnd)
            {
                iqueue_del(&seg->node);
                kcp->nrcv_buf--;
                iqueue_add_tail(&seg->node, &kcp->rcv_queue);
                kcp->nrcv_que++;
                kcp->rcv_nxt++;
            }
            else
            {
                break;
            }
        }

        if (kcp->nrcv_que < kcp->rcv_wnd && recover != 0)
        {
            kcp->probe |= KcpConstants.IKCP_ASK_TELL;
        }

        return len;
    }

    public static int ikcp_peeksize(IKCPCB* kcp)
    {
        IQueueHead* p;
        IKCPSEG* seg;
        int length = 0;

        if (iqueue_is_empty(&kcp->rcv_queue)) return -1;

        seg = iqueue_entry(kcp->rcv_queue.next);
        if (seg->frg == 0) return (int)seg->len;

        if (kcp->nrcv_que < seg->frg + 1) return -1;

        for (p = kcp->rcv_queue.next; p != &kcp->rcv_queue; p = p->next)
        {
            seg = iqueue_entry(p);
            length += (int)seg->len;
            if (seg->frg == 0) break;
        }

        return length;
    }

    public static int ikcp_send(IKCPCB* kcp, byte* buffer, int len)
    {
        IKCPSEG* seg;
        int count, i;
        int sent = 0;

        if (len < 0) return -1;
        if (kcp->mss == 0) return -1;

        if (kcp->stream != 0)
        {
            if (!iqueue_is_empty(&kcp->snd_queue))
            {
                IKCPSEG* old = iqueue_entry(kcp->snd_queue.prev);
                if (old->len < kcp->mss)
                {
                    int capacity = (int)(kcp->mss - old->len);
                    int extend = (len < capacity) ? len : capacity;
                    seg = ikcp_segment_new(kcp, (int)(old->len + extend));
                    if (seg == null) return -2;

                    iqueue_add_tail(&seg->node, &kcp->snd_queue);
                    Buffer.MemoryCopy(((byte*)old) + sizeof(IKCPSEG), ((byte*)seg) + sizeof(IKCPSEG), old->len, old->len);
                    if (buffer != null)
                    {
                        Buffer.MemoryCopy(buffer, ((byte*)seg) + sizeof(IKCPSEG) + old->len, extend, extend);
                        buffer += extend;
                    }
                    seg->len = old->len + (uint)extend;
                    seg->frg = 0;
                    len -= extend;
                    iqueue_del_init(&old->node);
                    ikcp_segment_delete(kcp, old);
                    sent = extend;
                }
            }
            if (len <= 0) return sent;
        }

        if (len <= (int)kcp->mss) count = 1;
        else count = (int)((len + kcp->mss - 1) / kcp->mss);

        if (count >= KcpConstants.IKCP_WND_RCV)
        {
            if (kcp->stream != 0 && sent > 0) return sent;
            return -2;
        }

        if (count == 0) count = 1;

        for (i = 0; i < count; i++)
        {
            int size = len > (int)kcp->mss ? (int)kcp->mss : len;
            seg = ikcp_segment_new(kcp, size);
            if (seg == null) return -2;

            if (buffer != null && len > 0)
            {
                Buffer.MemoryCopy(buffer, ((byte*)seg) + sizeof(IKCPSEG), size, size);
            }
            seg->len = (uint)size;
            seg->frg = (uint)((kcp->stream == 0) ? (count - i - 1) : 0);
            iqueue_init(&seg->node);
            iqueue_add_tail(&seg->node, &kcp->snd_queue);
            kcp->nsnd_que++;
            if (buffer != null) buffer += size;
            len -= size;
            sent += size;
        }

        return sent;
    }

    private static void ikcp_update_ack(IKCPCB* kcp, int rtt)
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
        rto = kcp->rx_srtt + (int)_imax_(kcp->interval, (uint)(4 * kcp->rx_rttval));
        kcp->rx_rto = (int)_ibound_((uint)kcp->rx_minrto, (uint)rto, KcpConstants.IKCP_RTO_MAX);
        if (kcp->ccops != null && kcp->ccops->on_rtt != null)
        {
            kcp->ccops->on_rtt(kcp, rtt);
        }
    }

    private static void ikcp_shrink_buf(IKCPCB* kcp)
    {
        IQueueHead* p = kcp->snd_buf.next;
        if (p != &kcp->snd_buf)
        {
            IKCPSEG* seg = iqueue_entry(p);
            kcp->snd_una = seg->sn;
        }
        else
        {
            kcp->snd_una = kcp->snd_nxt;
        }
    }

    private static void ikcp_parse_ack(IKCPCB* kcp, uint sn)
    {
        IQueueHead* p, next;
        int pkt_rtt;

        if (_itimediff(sn, kcp->snd_una) < 0 || _itimediff(sn, kcp->snd_nxt) >= 0)
            return;

        for (p = kcp->snd_buf.next; p != &kcp->snd_buf; p = next)
        {
            IKCPSEG* seg = iqueue_entry(p);
            next = p->next;
            if (sn == seg->sn)
            {
                kcp->ackedlen += seg->len;
                if (kcp->ccops != null && kcp->ccops->on_pkt_acked != null)
                {
                    pkt_rtt = -1;
                    if (_itimediff(kcp->current, seg->ts) >= 0)
                    {
                        pkt_rtt = _itimediff(kcp->current, seg->ts);
                    }
                    kcp->ccops->on_pkt_acked(kcp, seg->sn, seg->ts, seg->len, pkt_rtt, seg->xmit);
                }
                iqueue_del(p);
                ikcp_segment_delete(kcp, seg);
                kcp->nsnd_buf--;
                break;
            }
            if (_itimediff(sn, seg->sn) < 0)
            {
                break;
            }
        }
    }

    private static void ikcp_parse_una(IKCPCB* kcp, uint una)
    {
        IQueueHead* p, next;
        for (p = kcp->snd_buf.next; p != &kcp->snd_buf; p = next)
        {
            IKCPSEG* seg = iqueue_entry(p);
            next = p->next;
            if (_itimediff(una, seg->sn) > 0)
            {
                kcp->ackedlen += seg->len;
                if (kcp->ccops != null && kcp->ccops->on_pkt_acked != null)
                {
                    kcp->ccops->on_pkt_acked(kcp, seg->sn, seg->ts, seg->len, -1, seg->xmit);
                }
                iqueue_del(p);
                ikcp_segment_delete(kcp, seg);
                kcp->nsnd_buf--;
            }
            else
            {
                break;
            }
        }
    }

    private static void ikcp_parse_fastack(IKCPCB* kcp, uint sn, uint ts)
    {
        IQueueHead* p, next;

        if (_itimediff(sn, kcp->snd_una) < 0 || _itimediff(sn, kcp->snd_nxt) >= 0)
            return;

        for (p = kcp->snd_buf.next; p != &kcp->snd_buf; p = next)
        {
            IKCPSEG* seg = iqueue_entry(p);
            next = p->next;
            if (_itimediff(sn, seg->sn) < 0)
            {
                break;
            }
            else if (sn != seg->sn)
            {
                if (_itimediff(ts, seg->ts) >= 0)
                    seg->fastack++;
            }
        }
    }

    private static void ikcp_ack_push(IKCPCB* kcp, uint sn, uint ts)
    {
        uint newsize = kcp->ackcount + 1;
        uint* ptr;

        if (newsize > kcp->ackblock)
        {
            uint* acklist;
            uint newblock;

            for (newblock = 8; newblock < newsize; newblock <<= 1) ;
            acklist = (uint*)ikcp_malloc(newblock * sizeof(uint) * 2);

            if (acklist == null)
            {
                return;
            }

            if (kcp->acklist != null)
            {
                uint x;
                for (x = 0; x < kcp->ackcount; x++)
                {
                    acklist[x * 2 + 0] = kcp->acklist[x * 2 + 0];
                    acklist[x * 2 + 1] = kcp->acklist[x * 2 + 1];
                }
                ikcp_free(kcp->acklist);
            }

            kcp->acklist = acklist;
            kcp->ackblock = newblock;
        }

        ptr = &kcp->acklist[kcp->ackcount * 2];
        ptr[0] = sn;
        ptr[1] = ts;
        kcp->ackcount++;
    }

    private static void ikcp_ack_get(IKCPCB* kcp, int p, uint* sn, uint* ts)
    {
        if (sn != null) sn[0] = kcp->acklist[p * 2 + 0];
        if (ts != null) ts[0] = kcp->acklist[p * 2 + 1];
    }

    private static void ikcp_parse_data(IKCPCB* kcp, IKCPSEG* newseg)
    {
        IQueueHead* p, prev;
        uint sn = newseg->sn;
        int repeat = 0;

        if (_itimediff(sn, kcp->rcv_nxt + kcp->rcv_wnd) >= 0 ||
            _itimediff(sn, kcp->rcv_nxt) < 0)
        {
            ikcp_segment_delete(kcp, newseg);
            return;
        }

        for (p = kcp->rcv_buf.prev; p != &kcp->rcv_buf; p = prev)
        {
            IKCPSEG* seg = iqueue_entry(p);
            prev = p->prev;
            if (seg->sn == sn)
            {
                repeat = 1;
                break;
            }
            if (_itimediff(sn, seg->sn) > 0)
            {
                break;
            }
        }

        if (repeat == 0)
        {
            iqueue_init(&newseg->node);
            iqueue_add(&newseg->node, p);
            kcp->nrcv_buf++;
        }
        else
        {
            ikcp_segment_delete(kcp, newseg);
        }

        while (!iqueue_is_empty(&kcp->rcv_buf))
        {
            IKCPSEG* seg = iqueue_entry(kcp->rcv_buf.next);
            if (seg->sn == kcp->rcv_nxt && kcp->nrcv_que < kcp->rcv_wnd)
            {
                iqueue_del(&seg->node);
                kcp->nrcv_buf--;
                iqueue_add_tail(&seg->node, &kcp->rcv_queue);
                kcp->nrcv_que++;
                kcp->rcv_nxt++;
            }
            else
            {
                break;
            }
        }
    }

    public static int ikcp_input(IKCPCB* kcp, byte* data, int size)
    {
        uint prev_una = kcp->snd_una;
        uint prev_nsnd_buf = kcp->nsnd_buf;
        uint acked_segs, prior_in_flight;
        uint maxack = 0, latest_ts = 0;
        int flag = 0;

        kcp->ackedlen = 0;

        if (ikcp_canlog(kcp, KcpConstants.IKCP_LOG_INPUT) != 0)
        {
            ikcp_log(kcp, KcpConstants.IKCP_LOG_INPUT, "[RI] {0} bytes", size);
        }

        if (data == null || size < (int)KcpConstants.IKCP_OVERHEAD) return -1;

        while (true)
        {
            uint ts, sn, len, una, conv;
            ushort wnd;
            byte cmd, frg;
            IKCPSEG* seg;

            if (size < (int)KcpConstants.IKCP_OVERHEAD) break;

            data = ikcp_decode32u(data, &conv);
            if (conv != kcp->conv) return -1;

            data = ikcp_decode8u(data, &cmd);
            data = ikcp_decode8u(data, &frg);
            data = ikcp_decode16u(data, &wnd);
            data = ikcp_decode32u(data, &ts);
            data = ikcp_decode32u(data, &sn);
            data = ikcp_decode32u(data, &una);
            data = ikcp_decode32u(data, &len);

            size -= (int)KcpConstants.IKCP_OVERHEAD;

            if ((long)size < (long)len || (int)len < 0) return -2;

            if (cmd != KcpConstants.IKCP_CMD_PUSH && cmd != KcpConstants.IKCP_CMD_ACK &&
                cmd != KcpConstants.IKCP_CMD_WASK && cmd != KcpConstants.IKCP_CMD_WINS)
                return -3;

            kcp->rmt_wnd = wnd;
            ikcp_parse_una(kcp, una);
            ikcp_shrink_buf(kcp);

            if (cmd == KcpConstants.IKCP_CMD_ACK)
            {
                if (_itimediff(kcp->current, ts) >= 0)
                {
                    ikcp_update_ack(kcp, _itimediff(kcp->current, ts));
                }
                ikcp_parse_ack(kcp, sn);
                ikcp_shrink_buf(kcp);
                if (flag == 0)
                {
                    flag = 1;
                    maxack = sn;
                    latest_ts = ts;
                }
                else
                {
                    if (_itimediff(sn, maxack) > 0)
                    {
                        if (_itimediff(ts, latest_ts) > 0)
                        {
                            maxack = sn;
                            latest_ts = ts;
                        }
                    }
                }
                if (ikcp_canlog(kcp, KcpConstants.IKCP_LOG_IN_ACK) != 0)
                {
                    ikcp_log(kcp, KcpConstants.IKCP_LOG_IN_ACK, "input ack: sn={0} rtt={1} rto={2}",
                        sn, _itimediff(kcp->current, ts), kcp->rx_rto);
                }
            }
            else if (cmd == KcpConstants.IKCP_CMD_PUSH)
            {
                if (ikcp_canlog(kcp, KcpConstants.IKCP_LOG_IN_DATA) != 0)
                {
                    ikcp_log(kcp, KcpConstants.IKCP_LOG_IN_DATA, "input psh: sn={0} ts={1}", sn, ts);
                }
                if (_itimediff(sn, kcp->rcv_nxt + kcp->rcv_wnd) < 0)
                {
                    ikcp_ack_push(kcp, sn, ts);
                    if (_itimediff(sn, kcp->rcv_nxt) >= 0)
                    {
                        seg = ikcp_segment_new(kcp, (int)len);
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
                            Buffer.MemoryCopy(data, ((byte*)seg) + sizeof(IKCPSEG), len, len);
                        }

                        ikcp_parse_data(kcp, seg);
                    }
                }
            }
            else if (cmd == KcpConstants.IKCP_CMD_WASK)
            {
                kcp->probe |= KcpConstants.IKCP_ASK_TELL;
                if (ikcp_canlog(kcp, KcpConstants.IKCP_LOG_IN_PROBE) != 0)
                {
                    ikcp_log(kcp, KcpConstants.IKCP_LOG_IN_PROBE, "input probe");
                }
            }
            else if (cmd == KcpConstants.IKCP_CMD_WINS)
            {
                if (ikcp_canlog(kcp, KcpConstants.IKCP_LOG_IN_WINS) != 0)
                {
                    ikcp_log(kcp, KcpConstants.IKCP_LOG_IN_WINS, "input wins: {0}", wnd);
                }
            }
            else
            {
                return -3;
            }

            data += (int)len;
            size -= (int)len;
        }

        if (flag != 0)
        {
            ikcp_parse_fastack(kcp, maxack, latest_ts);
        }

        if (_itimediff(kcp->snd_una, prev_una) > 0)
        {
            acked_segs = kcp->snd_una - prev_una;
            prior_in_flight = prev_nsnd_buf;
            if (kcp->ccops != null && kcp->ccops->on_ack != null)
            {
                kcp->ccops->on_ack(kcp, acked_segs, kcp->ackedlen, prior_in_flight);
            }
            else
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
        }

        return 0;
    }

    private static byte* ikcp_encode_seg(byte* ptr, IKCPSEG* seg)
    {
        ptr = ikcp_encode32u(ptr, seg->conv);
        ptr = ikcp_encode8u(ptr, (byte)seg->cmd);
        ptr = ikcp_encode8u(ptr, (byte)seg->frg);
        ptr = ikcp_encode16u(ptr, (ushort)seg->wnd);
        ptr = ikcp_encode32u(ptr, seg->ts);
        ptr = ikcp_encode32u(ptr, seg->sn);
        ptr = ikcp_encode32u(ptr, seg->una);
        ptr = ikcp_encode32u(ptr, seg->len);
        return ptr;
    }

    private static int ikcp_wnd_unused(IKCPCB* kcp)
    {
        if (kcp->nrcv_que < kcp->rcv_wnd)
        {
            return (int)(kcp->rcv_wnd - kcp->nrcv_que);
        }
        return 0;
    }

    public static void ikcp_flush(IKCPCB* kcp)
    {
        uint current = kcp->current;
        byte* buffer = kcp->buffer;
        byte* ptr = buffer;
        int count, size, i;
        uint resent, cwnd;
        uint rtomin;
        uint prior_cwnd;
        uint eff_cwnd, cur_inflight;
        int pacing_budget = -1;
        IQueueHead* p;
        int change = 0;
        int lost = 0;
        IKCPSEG seg;

        if (kcp->updated == 0) return;

        if (kcp->ccops != null && kcp->ccops->on_tick != null)
        {
            kcp->ccops->on_tick(kcp);
        }

        if (kcp->ccops != null && kcp->ccops->pacing_rate != null)
        {
            pacing_budget = (int)kcp->ccops->pacing_rate(kcp);
        }

        prior_cwnd = kcp->cwnd;

        seg.conv = kcp->conv;
        seg.cmd = KcpConstants.IKCP_CMD_ACK;
        seg.frg = 0;
        seg.wnd = (uint)ikcp_wnd_unused(kcp);
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
                ikcp_output(kcp, buffer, size);
                ptr = buffer;
            }
            ikcp_ack_get(kcp, i, &seg.sn, &seg.ts);
            ptr = ikcp_encode_seg(ptr, &seg);
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
                if (_itimediff(kcp->current, kcp->ts_probe) >= 0)
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
                ikcp_output(kcp, buffer, size);
                ptr = buffer;
            }
            ptr = ikcp_encode_seg(ptr, &seg);
        }

        if ((kcp->probe & KcpConstants.IKCP_ASK_TELL) != 0)
        {
            seg.cmd = KcpConstants.IKCP_CMD_WINS;
            size = (int)(ptr - buffer);
            if (size + (int)KcpConstants.IKCP_OVERHEAD > (int)kcp->mtu)
            {
                ikcp_output(kcp, buffer, size);
                ptr = buffer;
            }
            ptr = ikcp_encode_seg(ptr, &seg);
        }

        kcp->probe = 0;

        cwnd = _imin_(kcp->snd_wnd, kcp->rmt_wnd);
        if (kcp->ccops != null || kcp->nocwnd == 0) cwnd = _imin_(kcp->cwnd, cwnd);

        while (_itimediff(kcp->snd_nxt, kcp->snd_una + cwnd) < 0)
        {
            IKCPSEG* newseg;
            if (iqueue_is_empty(&kcp->snd_queue)) break;

            newseg = iqueue_entry(kcp->snd_queue.next);

            iqueue_del(&newseg->node);
            iqueue_add_tail(&newseg->node, &kcp->snd_buf);
            kcp->nsnd_que--;
            kcp->nsnd_buf++;

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
        }

        if (kcp->ccops != null && kcp->ccops->on_app_limited != null)
        {
            if (iqueue_is_empty(&kcp->snd_queue))
            {
                eff_cwnd = _imin_(kcp->snd_wnd, kcp->rmt_wnd);
                eff_cwnd = _imin_(kcp->cwnd, eff_cwnd);
                cur_inflight = kcp->nsnd_buf;
                if (cur_inflight < eff_cwnd)
                {
                    kcp->ccops->on_app_limited(kcp, cur_inflight);
                }
            }
        }

        resent = (kcp->fastresend > 0) ? (uint)kcp->fastresend : 0xffffffff;
        rtomin = (kcp->nodelay == 0) ? (uint)(kcp->rx_rto >> 3) : 0;

        for (p = kcp->snd_buf.next; p != &kcp->snd_buf; p = p->next)
        {
            IKCPSEG* segment = iqueue_entry(p);
            int needsend = 0;
            if (segment->xmit == 0)
            {
                needsend = 1;
                segment->xmit++;
                segment->rto = (uint)kcp->rx_rto;
                segment->resendts = current + segment->rto + rtomin;
            }
            else if (_itimediff(current, segment->resendts) >= 0)
            {
                needsend = 1;
                segment->xmit++;
                kcp->xmit++;
                if (kcp->nodelay == 0)
                {
                    segment->rto += _imax_(segment->rto, (uint)kcp->rx_rto);
                }
                else
                {
                    int step = (kcp->nodelay < 2) ? (int)segment->rto : kcp->rx_rto;
                    segment->rto += (uint)(step / 2);
                }
                segment->resendts = current + segment->rto;
                lost = 1;
            }
            else if (segment->fastack >= resent)
            {
                if ((int)segment->xmit <= kcp->fastlimit ||
                    kcp->fastlimit <= 0)
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

                if (pacing_budget >= 0 && pacing_budget < (int)segment->len)
                {
                    break;
                }

                if (kcp->ccops != null && kcp->ccops->on_pkt_sent != null)
                {
                    kcp->ccops->on_pkt_sent(kcp, segment->sn, current,
                            segment->len, kcp->nsnd_buf, segment->xmit);
                }

                size = (int)(ptr - buffer);
                need = (int)(KcpConstants.IKCP_OVERHEAD + segment->len);

                if (size + need > (int)kcp->mtu)
                {
                    ikcp_output(kcp, buffer, size);
                    ptr = buffer;
                }

                ptr = ikcp_encode_seg(ptr, segment);

                if (segment->len > 0)
                {
                    Buffer.MemoryCopy(((byte*)segment) + sizeof(IKCPSEG), ptr, segment->len, segment->len);
                    ptr += (int)segment->len;
                }

                if (pacing_budget >= 0)
                {
                    pacing_budget -= (int)segment->len;
                }

                if (segment->xmit >= kcp->dead_link)
                {
                    kcp->state = unchecked((uint)-1);
                }
            }
        }

        size = (int)(ptr - buffer);
        if (size > 0)
        {
            ikcp_output(kcp, buffer, size);
        }

        if (change != 0)
        {
            if (kcp->ccops != null && kcp->ccops->on_fast_retransmit != null)
            {
                kcp->ccops->on_fast_retransmit(kcp, (uint)change, kcp->nsnd_buf, prior_cwnd);
            }
            else
            {
                uint inflight = kcp->snd_nxt - kcp->snd_una;
                kcp->ssthresh = inflight / 2;
                if (kcp->ssthresh < KcpConstants.IKCP_THRESH_MIN)
                    kcp->ssthresh = KcpConstants.IKCP_THRESH_MIN;
                kcp->cwnd = kcp->ssthresh + resent;
                kcp->incr = kcp->cwnd * kcp->mss;
            }
        }

        if (lost != 0)
        {
            if (kcp->ccops != null && kcp->ccops->on_timeout != null)
            {
                kcp->ccops->on_timeout(kcp, prior_cwnd);
            }
            else
            {
                kcp->ssthresh = prior_cwnd / 2;
                if (kcp->ssthresh < KcpConstants.IKCP_THRESH_MIN)
                    kcp->ssthresh = KcpConstants.IKCP_THRESH_MIN;
                kcp->cwnd = 1;
                kcp->incr = kcp->mss;
            }
        }

        if (kcp->cwnd < 1)
        {
            kcp->cwnd = 1;
            kcp->incr = kcp->mss;
        }
    }

    public static void ikcp_update(IKCPCB* kcp, uint current)
    {
        int slap;

        kcp->current = current;

        if (kcp->updated == 0)
        {
            kcp->updated = 1;
            kcp->ts_flush = kcp->current;
        }

        slap = _itimediff(kcp->current, kcp->ts_flush);

        if (slap >= 10000 || slap < -10000)
        {
            kcp->ts_flush = kcp->current;
            slap = 0;
        }

        if (slap >= 0)
        {
            kcp->ts_flush += kcp->interval;
            if (_itimediff(kcp->current, kcp->ts_flush) >= 0)
            {
                kcp->ts_flush = kcp->current + kcp->interval;
            }
            ikcp_flush(kcp);
        }
    }

    public static uint ikcp_check(IKCPCB* kcp, uint current)
    {
        uint ts_flush = kcp->ts_flush;
        int tm_flush = 0x7fffffff;
        int tm_packet = 0x7fffffff;
        uint minimal = 0;
        IQueueHead* p;

        if (kcp->updated == 0)
        {
            return current;
        }

        if (_itimediff(current, ts_flush) >= 10000 ||
            _itimediff(current, ts_flush) < -10000)
        {
            ts_flush = current;
        }

        if (_itimediff(current, ts_flush) >= 0)
        {
            return current;
        }

        tm_flush = _itimediff(ts_flush, current);

        for (p = kcp->snd_buf.next; p != &kcp->snd_buf; p = p->next)
        {
            IKCPSEG* seg = iqueue_entry(p);
            int diff = _itimediff(seg->resendts, current);
            if (diff <= 0)
            {
                return current;
            }
            if (diff < tm_packet) tm_packet = diff;
        }

        minimal = (uint)(tm_packet < tm_flush ? tm_packet : tm_flush);
        if (minimal >= kcp->interval) minimal = kcp->interval;

        return current + minimal;
    }

    public static int ikcp_setmtu(IKCPCB* kcp, int mtu)
    {
        byte* buffer;
        if (mtu < 50 || mtu < (int)KcpConstants.IKCP_OVERHEAD)
            return -1;
        buffer = (byte*)ikcp_malloc((nuint)((mtu + KcpConstants.IKCP_OVERHEAD) * 3));
        if (buffer == null)
            return -2;
        kcp->mtu = (uint)mtu;
        kcp->mss = kcp->mtu - KcpConstants.IKCP_OVERHEAD;
        ikcp_free(kcp->buffer);
        kcp->buffer = buffer;
        return 0;
    }

    public static int ikcp_interval(IKCPCB* kcp, int interval)
    {
        if (interval > 5000) interval = 5000;
        else if (interval < 10) interval = 10;
        kcp->interval = (uint)interval;
        return 0;
    }

    public static int ikcp_nodelay(IKCPCB* kcp, int nodelay, int interval, int resend, int nc)
    {
        if (nodelay >= 0)
        {
            kcp->nodelay = (uint)nodelay;
            if (nodelay != 0)
            {
                kcp->rx_minrto = (int)KcpConstants.IKCP_RTO_NDL;
            }
            else
            {
                kcp->rx_minrto = (int)KcpConstants.IKCP_RTO_MIN;
            }
        }
        if (interval >= 0)
        {
            if (interval > 5000) interval = 5000;
            else if (interval < 10) interval = 10;
            kcp->interval = (uint)interval;
        }
        if (resend >= 0)
        {
            kcp->fastresend = resend;
        }
        if (nc >= 0)
        {
            kcp->nocwnd = nc;
        }
        return 0;
    }

    public static int ikcp_wndsize(IKCPCB* kcp, int sndwnd, int rcvwnd)
    {
        if (kcp != null)
        {
            if (sndwnd > 0)
            {
                kcp->snd_wnd = (uint)sndwnd;
            }
            if (rcvwnd > 0)
            {
                kcp->rcv_wnd = _imax_((uint)rcvwnd, KcpConstants.IKCP_WND_RCV);
            }
        }
        return 0;
    }

    public static int ikcp_waitsnd(IKCPCB* kcp)
    {
        return (int)(kcp->nsnd_buf + kcp->nsnd_que);
    }

    public static uint ikcp_getconv(void* ptr)
    {
        uint conv;
        ikcp_decode32u((byte*)ptr, &conv);
        return conv;
    }

    public static int ikcp_setcc(IKCPCB* kcp, IKCPOPS* ops)
    {
        if (kcp == null) return -1;
        if (kcp->ccops != null && kcp->ccops->release != null)
        {
            kcp->ccops->release(kcp);
        }
        kcp->congest = null;
        kcp->ccops = ops;
        if (ops != null)
        {
            if (ops->init != null)
            {
                if (ops->init(kcp) < 0)
                {
                    kcp->ccops = null;
                    kcp->congest = null;
                    if (kcp->cwnd < 1) kcp->cwnd = 1;
                    kcp->incr = kcp->cwnd * kcp->mss;
                    return -1;
                }
            }
        }
        else
        {
            if (kcp->cwnd < 1) kcp->cwnd = 1;
            kcp->incr = kcp->cwnd * kcp->mss;
            if (kcp->incr < kcp->mss) kcp->incr = kcp->mss;
        }
        return 0;
    }
}
