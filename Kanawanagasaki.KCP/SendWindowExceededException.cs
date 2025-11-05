namespace Kanawanagasaki.KCP;

using System;

[Serializable]
internal class SendWindowExceededException : Exception
{
    internal SendWindowExceededException()
    {
    }

    internal SendWindowExceededException(string? message) : base(message)
    {
    }

    internal SendWindowExceededException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}