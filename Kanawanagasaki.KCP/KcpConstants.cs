namespace Kanawanagasaki.KCP;

public static class KcpConstants
{
    public const uint IKCP_RTO_NDL = 30;
    public const uint IKCP_RTO_MIN = 100;
    public const uint IKCP_RTO_DEF = 200;
    public const uint IKCP_RTO_MAX = 60000;
    public const uint IKCP_CMD_PUSH = 81;
    public const uint IKCP_CMD_ACK = 82;
    public const uint IKCP_CMD_WASK = 83;
    public const uint IKCP_CMD_WINS = 84;
    public const uint IKCP_ASK_SEND = 1;
    public const uint IKCP_ASK_TELL = 2;
    public const uint IKCP_WND_SND = 32;
    public const uint IKCP_WND_RCV = 128;
    public const uint IKCP_MTU_DEF = 1400;
    public const uint IKCP_ACK_FAST = 3;
    public const uint IKCP_INTERVAL = 100;
    public const uint IKCP_OVERHEAD = 24;
    public const uint IKCP_DEADLINK = 20;
    public const uint IKCP_THRESH_INIT = 2;
    public const uint IKCP_THRESH_MIN = 2;
    public const uint IKCP_PROBE_INIT = 5000;
    public const uint IKCP_PROBE_LIMIT = 120000;
    public const uint IKCP_FASTACK_LIMIT = 5;

    public const int IKCP_LOG_OUTPUT = 1;
    public const int IKCP_LOG_INPUT = 2;
    public const int IKCP_LOG_SEND = 4;
    public const int IKCP_LOG_RECV = 8;
    public const int IKCP_LOG_IN_DATA = 16;
    public const int IKCP_LOG_IN_ACK = 32;
    public const int IKCP_LOG_IN_PROBE = 64;
    public const int IKCP_LOG_IN_WINS = 128;
    public const int IKCP_LOG_OUT_DATA = 256;
    public const int IKCP_LOG_OUT_ACK = 512;
    public const int IKCP_LOG_OUT_PROBE = 1024;
    public const int IKCP_LOG_OUT_WINS = 2048;
}
