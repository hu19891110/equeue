﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ECommon.Components;
using ECommon.Logging;
using ECommon.Remoting;
using ECommon.Serializing;
using EQueue.Broker.Client;
using EQueue.Broker.LongPolling;
using EQueue.Protocols;
using EQueue.Utils;

namespace EQueue.Broker.Processors
{
    public class PullMessageRequestHandler : IRequestHandler
    {
        private ConsumerManager _consumerManager;
        private SuspendedPullRequestManager _suspendedPullRequestManager;
        private IMessageService _messageService;
        private IQueueService _queueService;
        private IOffsetManager _offsetManager;
        private IBinarySerializer _binarySerializer;
        private ILogger _logger;
        private readonly byte[] EmptyResponseData;

        public PullMessageRequestHandler()
        {
            _consumerManager = ObjectContainer.Resolve<ConsumerManager>();
            _suspendedPullRequestManager = ObjectContainer.Resolve<SuspendedPullRequestManager>();
            _messageService = ObjectContainer.Resolve<IMessageService>();
            _queueService = ObjectContainer.Resolve<IQueueService>();
            _offsetManager = ObjectContainer.Resolve<IOffsetManager>();
            _binarySerializer = ObjectContainer.Resolve<IBinarySerializer>();
            _logger = ObjectContainer.Resolve<ILoggerFactory>().Create(GetType().FullName);
            EmptyResponseData = new byte[0];
        }

        public RemotingResponse HandleRequest(IRequestHandlerContext context, RemotingRequest remotingRequest)
        {
            var request = DeserializePullMessageRequest(remotingRequest.Body);
            var topic = request.MessageQueue.Topic;
            var queueId = request.MessageQueue.QueueId;
            var pullOffset = request.QueueOffset;

            //如果消费者第一次过来拉取消息，则计算下一个应该拉取的位置，并返回给消费者
            if (pullOffset < 0)
            {
                var nextConsumeOffset = GetNextConsumeOffset(topic, queueId, request.ConsumerGroup, request.ConsumeFromWhere);
                return BuildNextOffsetResetResponse(remotingRequest, nextConsumeOffset);
            }

            //尝试拉取消息
            var messages = _messageService.GetMessages(topic, queueId, pullOffset, request.PullMessageBatchSize);

            //如果消息存在，则返回消息
            if (messages.Count() > 0)
            {
                return BuildFoundResponse(remotingRequest, messages);
            }

            //消息不存在，如果挂起时间大于0，则挂起请求
            if (request.SuspendPullRequestMilliseconds > 0)
            {
                var pullRequest = new PullRequest(
                    remotingRequest,
                    request,
                    context,
                    DateTime.Now,
                    request.SuspendPullRequestMilliseconds,
                    ExecutePullRequest,
                    ExecutePullRequest,
                    ExecuteReplacedPullRequest);
                _suspendedPullRequestManager.SuspendPullRequest(pullRequest);
                return null;
            }

            var queueMinOffset = _queueService.GetQueueMinOffset(topic, queueId);
            var queueCurrentOffset = _queueService.GetQueueCurrentOffset(topic, queueId);

            if (pullOffset < queueMinOffset)
            {
                return BuildNextOffsetResetResponse(remotingRequest, queueMinOffset);
            }
            else if (pullOffset > queueCurrentOffset + 1)
            {
                return BuildNextOffsetResetResponse(remotingRequest, queueCurrentOffset + 1);
            }
            else
            {
                return BuildNoNewMessageResponse(remotingRequest);
            }
        }

        private void ExecutePullRequest(PullRequest pullRequest)
        {
            if (!IsPullRequestValid(pullRequest))
            {
                return;
            }

            var pullMessageRequest = pullRequest.PullMessageRequest;
            var topic = pullMessageRequest.MessageQueue.Topic;
            var queueId = pullMessageRequest.MessageQueue.QueueId;
            var pullOffset = pullMessageRequest.QueueOffset;

            var messages = _messageService.GetMessages(topic, queueId, pullOffset, pullMessageRequest.PullMessageBatchSize);

            if (messages.Count() > 0)
            {
                SendRemotingResponse(pullRequest, BuildFoundResponse(pullRequest.RemotingRequest, messages));
                return;
            }

            var queueMinOffset = _queueService.GetQueueMinOffset(topic, queueId);
            var queueCurrentOffset = _queueService.GetQueueCurrentOffset(topic, queueId);

            if (pullOffset < queueMinOffset)
            {
                SendRemotingResponse(pullRequest, BuildNextOffsetResetResponse(pullRequest.RemotingRequest, queueMinOffset));
            }
            else if (pullOffset > queueCurrentOffset + 1)
            {
                SendRemotingResponse(pullRequest, BuildNextOffsetResetResponse(pullRequest.RemotingRequest, queueCurrentOffset + 1));
            }
            else
            {
                SendRemotingResponse(pullRequest, BuildNoNewMessageResponse(pullRequest.RemotingRequest));
            }
        }
        private void ExecuteReplacedPullRequest(PullRequest pullRequest)
        {
            if (!IsPullRequestValid(pullRequest))
            {
                return;
            }
            SendRemotingResponse(pullRequest, BuildIgnoredResponse(pullRequest.RemotingRequest));
        }
        private bool IsPullRequestValid(PullRequest pullRequest)
        {
            var consumerGroup = _consumerManager.GetConsumerGroup(pullRequest.PullMessageRequest.ConsumerGroup);
            return consumerGroup != null && consumerGroup.IsConsumerActive(pullRequest.RequestHandlerContext.Connection.RemotingEndPoint.ToString());
        }
        private RemotingResponse BuildNoNewMessageResponse(RemotingRequest remotingRequest)
        {
            return RemotingResponseFactory.CreateResponse(remotingRequest, (short)PullStatus.NoNewMessage);
        }
        private RemotingResponse BuildIgnoredResponse(RemotingRequest remotingRequest)
        {
            return RemotingResponseFactory.CreateResponse(remotingRequest, (short)PullStatus.Ignored);
        }
        private RemotingResponse BuildNextOffsetResetResponse(RemotingRequest remotingRequest, long nextOffset)
        {
            return RemotingResponseFactory.CreateResponse(remotingRequest, (short)PullStatus.NextOffsetReset, BitConverter.GetBytes(nextOffset));
        }
        private RemotingResponse BuildFoundResponse(RemotingRequest remotingRequest, IEnumerable<QueueMessage> messages)
        {
            return RemotingResponseFactory.CreateResponse(remotingRequest, (short)PullStatus.Found, _binarySerializer.Serialize(messages));
        }
        private void SendRemotingResponse(PullRequest pullRequest, RemotingResponse remotingResponse)
        {
            pullRequest.RequestHandlerContext.SendRemotingResponse(remotingResponse);
        }
        private long GetNextConsumeOffset(string topic, int queueId, string consumerGroup, ConsumeFromWhere consumerFromWhere)
        {
            var lastConsumedQueueOffset = _offsetManager.GetQueueOffset(topic, queueId, consumerGroup);
            if (lastConsumedQueueOffset >= 0)
            {
                var queueCurrentOffset = _queueService.GetQueueCurrentOffset(topic, queueId);
                return queueCurrentOffset < lastConsumedQueueOffset ? queueCurrentOffset + 1 : lastConsumedQueueOffset + 1;
            }

            if (consumerFromWhere == ConsumeFromWhere.FirstOffset)
            {
                var queueMinOffset = _queueService.GetQueueMinOffset(topic, queueId);
                if (queueMinOffset < 0)
                {
                    queueMinOffset = 0;
                }
                return queueMinOffset;
            }
            else
            {
                var queueCurrentOffset = _queueService.GetQueueCurrentOffset(topic, queueId);
                if (queueCurrentOffset < 0)
                {
                    queueCurrentOffset = 0;
                }
                else
                {
                    queueCurrentOffset++;
                }
                return queueCurrentOffset;
            }
        }
        private static PullMessageRequest DeserializePullMessageRequest(byte[] data)
        {
            using (var stream = new MemoryStream(data))
            {
                return PullMessageRequest.ReadFromStream(stream);
            }
        }
    }
}
