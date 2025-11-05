namespace Kanawanagasaki.KCP;

using System;

[Serializable]
internal class SegmentSizeExceededException : Exception
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