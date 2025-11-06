namespace Kanawanagasaki.KCP;

using System;

[Serializable]
internal class KcpException : Exception
{
    public KcpException()
    {
    }

    public KcpException(string? message) : base(message)
    {
    }

    public KcpException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}