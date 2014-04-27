﻿namespace EQueue.Broker
{
    public interface IOffsetManager
    {
        void Recover();
        void Start();
        void Shutdown();
        void UpdateQueueOffset(string topic, int queueId, long offset, string group);
        long GetQueueOffset(string topic, int queueId, string group);
        long GetMinOffset(string topic, int queueId);
    }
}
