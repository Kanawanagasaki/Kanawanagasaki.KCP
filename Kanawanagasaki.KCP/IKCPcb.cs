namespace Kanawanagasaki.KCP;

using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct IKCPCB
{
    public uint conv, mtu, mss, state;
    public uint snd_una, snd_nxt, rcv_nxt;
    public uint ts_recent, ts_lastack, ssthresh;
    public int rx_rttval, rx_srtt, rx_rto, rx_minrto;
    public uint snd_wnd, rcv_wnd, rmt_wnd, cwnd, probe;
    public uint current, interval, ts_flush, xmit;
    public uint nrcv_buf, nsnd_buf;
    public uint nrcv_que, nsnd_que;
    public uint nodelay, updated;
    public uint ts_probe, probe_wait;
    public uint dead_link, incr;
    public IQueueHead snd_queue;
    public IQueueHead rcv_queue;
    public IQueueHead snd_buf;
    public IQueueHead rcv_buf;
    public uint* acklist;
    public uint ackcount;
    public uint ackblock;
    public void* user;
    public byte* buffer;
    public int fastresend;
    public int fastlimit;
    public int nocwnd, stream;
    public int logmask;
    public delegate* unmanaged[Cdecl]<byte*, int, IKCPCB*, void*, int> output;
    public delegate* unmanaged[Cdecl]<byte*, IKCPCB*, void*, void> writelog;
}
