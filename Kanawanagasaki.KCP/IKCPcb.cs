namespace Kanawanagasaki.KCP;

using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct IKCPcb
{
    internal uint conv;
    internal uint mtu;
    internal uint mss;
    internal uint state;
    internal uint snd_una;
    internal uint snd_nxt;
    internal uint rcv_nxt;
    internal uint ts_recent;
    internal uint ts_lastack;
    internal uint ssthresh;
    internal int rx_rttval;
    internal int rx_srtt;
    internal int rx_rto;
    internal int rx_minrto;
    internal uint snd_wnd;
    internal uint rcv_wnd;
    internal uint rmt_wnd;
    internal uint cwnd;
    internal uint probe;
    internal uint current;
    internal uint interval;
    internal uint ts_flush;
    internal uint xmit;
    internal uint nrcv_buf;
    internal uint nsnd_buf;
    internal uint nrcv_que;
    internal uint nsnd_que;
    internal uint nodelay;
    internal uint updated;
    internal uint ts_probe;
    internal uint probe_wait;
    internal uint dead_link;
    internal uint incr;

    internal IQueueHead snd_queue;
    internal IQueueHead rcv_queue;
    internal IQueueHead snd_buf;
    internal IQueueHead rcv_buf;

    internal uint* acklist;
    internal uint ackcount;
    internal uint ackblock;

    internal void* user;
    internal byte* buffer;

    internal int fastresend;
    internal int fastlimit;
    internal int nocwnd;
    internal int stream;
    internal int logmask;

    internal void* output_func;
    internal void* writelog_func;
}