﻿using System.Text;
using ECommon.Components;
using ECommon.Logging;
using ECommon.Remoting;
using EQueue.Protocols;

namespace EQueue.Broker.Processors
{
    public class GetTopicQueueIdsForConsumerRequestHandler : IRequestHandler
    {
        private IMessageService _messageService;

        public GetTopicQueueIdsForConsumerRequestHandler()
        {
            _messageService = ObjectContainer.Resolve<IMessageService>();
        }

        public RemotingResponse HandleRequest(IRequestHandlerContext context, RemotingRequest request)
        {
            var topic = Encoding.UTF8.GetString(request.Body);
            var queueIds = _messageService.GetQueueIdsForConsumer(topic);
            var data = Encoding.UTF8.GetBytes(string.Join(",", queueIds));
            return new RemotingResponse((int)ResponseCode.Success, request.Sequence, data);
        }
    }
}
