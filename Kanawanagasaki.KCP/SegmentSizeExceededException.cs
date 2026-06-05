namespace Kanawanagasaki.KCP;

using System;

[Serializable]
public class SegmentSizeExceededException : Exception
{
    internal SegmentSizeExceededException()
    {
    }

    internal SegmentSizeExceededException(string? message) : base(message)
    {
    }

    internal SegmentSizeExceededException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}