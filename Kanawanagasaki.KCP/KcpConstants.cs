namespace Kanawanagasaki.KCP;

internal static class KcpConstants
{
    internal const uint IKCP_RTO_NDL = 30;
    internal const uint IKCP_RTO_MIN = 100;
    internal const uint IKCP_RTO_DEF = 200;
    internal const uint IKCP_RTO_MAX = 60000;
    internal const uint IKCP_CMD_PUSH = 81;
    internal const uint IKCP_CMD_ACK = 82;
    internal const uint IKCP_CMD_WASK = 83;
    internal const uint IKCP_CMD_WINS = 84;
    internal const uint IKCP_ASK_SEND = 1;
    internal const uint IKCP_ASK_TELL = 2;
    internal const uint IKCP_WND_SND = 32;
    internal const uint IKCP_WND_RCV = 128;
    internal const uint IKCP_MTU_DEF = 1400;
    internal const uint IKCP_ACK_FAST = 3;
    internal const uint IKCP_INTERVAL = 100;
    internal const uint IKCP_OVERHEAD = 24;
    internal const uint IKCP_DEADLINK = 20;
    internal const uint IKCP_THRESH_INIT = 32;
    internal const uint IKCP_THRESH_MIN = 2;
    internal const uint IKCP_PROBE_INIT = 7000;
    internal const uint IKCP_PROBE_LIMIT = 120000;
    internal const uint IKCP_FASTACK_LIMIT = 5;

    internal const uint IKCP_LOG_OUTPUT = 1;
    internal const uint IKCP_LOG_INPUT = 2;
    internal const uint IKCP_LOG_SEND = 4;
    internal const uint IKCP_LOG_RECV = 8;
    internal const uint IKCP_LOG_IN_DATA = 16;
    internal const uint IKCP_LOG_IN_ACK = 32;
    internal const uint IKCP_LOG_IN_PROBE = 64;
    internal const uint IKCP_LOG_IN_WINS = 128;
    internal const uint IKCP_LOG_OUT_DATA = 256;
    internal const uint IKCP_LOG_OUT_ACK = 512;
    internal const uint IKCP_LOG_OUT_PROBE = 1024;
    internal const uint IKCP_LOG_OUT_WINS = 2048;
}
