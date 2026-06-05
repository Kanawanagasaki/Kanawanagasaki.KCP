namespace Kanawanagasaki.KCP;

using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct IKCPOPS
{
    public byte* name;
    public delegate* unmanaged[Cdecl]<IKCPCB*, int> init;
    public delegate* unmanaged[Cdecl]<IKCPCB*, void> release;
    public delegate* unmanaged[Cdecl]<IKCPCB*, uint, uint, uint, void> on_ack;
    public delegate* unmanaged[Cdecl]<IKCPCB*, uint, uint, uint, void> on_fast_retransmit;
    public delegate* unmanaged[Cdecl]<IKCPCB*, uint, void> on_timeout;
    public delegate* unmanaged[Cdecl]<IKCPCB*, void> on_tick;
    public delegate* unmanaged[Cdecl]<IKCPCB*, uint, void> on_app_limited;
    public delegate* unmanaged[Cdecl]<IKCPCB*, int, void> on_rtt;
    public delegate* unmanaged[Cdecl]<IKCPCB*, uint, uint, uint, uint, uint, void> on_pkt_sent;
    public delegate* unmanaged[Cdecl]<IKCPCB*, uint, uint, uint, int, uint, void> on_pkt_acked;
    public delegate* unmanaged[Cdecl]<IKCPCB*, void*, uint, uint> get_info;
    public delegate* unmanaged[Cdecl]<IKCPCB*, uint> pacing_rate;
}
