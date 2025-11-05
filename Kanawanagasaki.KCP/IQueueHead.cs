namespace Kanawanagasaki.KCP;

using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct IQueueHead
{
    internal IQueueHead* next;
    internal IQueueHead* prev;
}
