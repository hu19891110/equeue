﻿using System.Collections.Generic;
using System.Linq;
using ECommon.Components;
using ECommon.Remoting;
using ECommon.Serializing;
using EQueue.Protocols;

namespace EQueue.Broker.Processors
{
    public class QueryTopicQueueInfoRequestHandler : IRequestHandler
    {
        private IBinarySerializer _binarySerializer;
        private IMessageService _messageService;
        private IOffsetManager _offsetManager;

        public QueryTopicQueueInfoRequestHandler()
        {
            _binarySerializer = ObjectContainer.Resolve<IBinarySerializer>();
            _messageService = ObjectContainer.Resolve<IMessageService>();
            _offsetManager = ObjectContainer.Resolve<IOffsetManager>();
        }

        public RemotingResponse HandleRequest(IRequestHandlerContext context, RemotingRequest request)
        {
            var queryTopicConsumeInfoRequest = _binarySerializer.Deserialize<QueryTopicQueueInfoRequest>(request.Body);
            var topicQueueInfoList = new List<TopicQueueInfo>();
            var topicList = !string.IsNullOrEmpty(queryTopicConsumeInfoRequest.Topic) ? new List<string> { queryTopicConsumeInfoRequest.Topic } : _messageService.GetAllTopics().ToList();

            foreach (var topic in topicList)
            {
                var queues = _messageService.GetQueues(topic, false).ToList();
                foreach (var queue in queues)
                {
                    var topicQueueInfo = new TopicQueueInfo();
                    topicQueueInfo.Topic = queue.Topic;
                    topicQueueInfo.QueueId = queue.QueueId;
                    topicQueueInfo.QueueCurrentOffset = queue.CurrentOffset;
                    topicQueueInfo.QueueMessageCount = queue.GetMessageRealCount();
                    topicQueueInfo.QueueMaxConsumedOffset = _offsetManager.GetMinOffset(topic, queue.QueueId);
                    topicQueueInfo.Status = queue.Status;
                    topicQueueInfoList.Add(topicQueueInfo);
                }
            }

            var data = _binarySerializer.Serialize(topicQueueInfoList);
            return new RemotingResponse((int)ResponseCode.Success, request.Sequence, data);
        }
    }
}
