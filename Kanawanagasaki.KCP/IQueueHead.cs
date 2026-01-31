namespace Kanawanagasaki.KCP;

using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct IQueueHead
{
    public IQueueHead* next;
    public IQueueHead* prev;
}
