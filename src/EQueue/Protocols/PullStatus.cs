﻿namespace EQueue.Protocols
{
    public enum PullStatus : short
    {
        Found = 1,
        NoNewMessage = 2,
        NextOffsetReset = 3,
        Ignored = 4
    }
}
