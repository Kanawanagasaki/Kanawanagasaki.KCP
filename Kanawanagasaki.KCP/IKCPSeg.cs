namespace Kanawanagasaki.KCP;

using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct IKCPSeg
{
    internal IQueueHead node;
    internal uint conv;
    internal uint cmd;
    internal uint frg;
    internal uint wnd;
    internal uint ts;
    internal uint sn;
    internal uint una;
    internal uint len;
    internal uint resendts;
    internal uint rto;
    internal uint fastack;
    internal uint xmit;
    internal fixed byte data[1];
}
