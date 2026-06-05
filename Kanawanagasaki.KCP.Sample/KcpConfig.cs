namespace Kanawanagasaki.KCP.Sample;

public class KcpConfig
{
    public string LocalIp = "0.0.0.0";
    public int LocalPort = 10001;
    public string RemoteIp = "127.0.0.1";
    public int RemotePort = 10002;
    public uint ConversationId = 1;

    public bool NoDelay;
    public int IntervalMs = 100;
    public int FastResend;
    public bool NoCongestionControl;
    public int SendWindow = 32;
    public int ReceiveWindow = 128;
    public int Mtu = 1400;
    public bool StreamMode;

    public int BufferSize = 4096;
    public ECommunicationDirection Direction;

    public void CopyFrom(KcpConfig source)
    {
        LocalIp = source.LocalIp;
        LocalPort = source.LocalPort;
        RemoteIp = source.RemoteIp;
        RemotePort = source.RemotePort;
        ConversationId = source.ConversationId;

        NoDelay = source.NoDelay;
        IntervalMs = source.IntervalMs;
        FastResend = source.FastResend;
        NoCongestionControl = source.NoCongestionControl;
        SendWindow = source.SendWindow;
        ReceiveWindow = source.ReceiveWindow;
        Mtu = source.Mtu;
        StreamMode = source.StreamMode;

        BufferSize = source.BufferSize;
        Direction = source.Direction;
    }

    public enum ECommunicationDirection
    {
        Bidirectional,
        SendOnly,
        ReceiveOnly
    }
}
