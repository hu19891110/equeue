﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ECommon.Components;
using ECommon.Logging;
using ECommon.Remoting;
using ECommon.Remoting.Exceptions;
using ECommon.Scheduling;
using ECommon.Serializing;
using ECommon.Socketing;
using EQueue.Protocols;
using EQueue.Utils;

namespace EQueue.Clients.Producers
{
    public class Producer : ISocketClientEventListener
    {
        private readonly object _lockObject;
        private readonly ConcurrentDictionary<string, IList<int>> _topicQueueIdsDict;
        private readonly IScheduleService _scheduleService;
        private readonly SocketRemotingClient _remotingClient;
        private readonly IBinarySerializer _binarySerializer;
        private readonly IQueueSelector _queueSelector;
        private readonly ILogger _logger;
        private readonly List<int> _taskIds;

        public string Id { get; private set; }
        public ProducerSetting Setting { get; private set; }

        public Producer(string id) : this(id, null) { }
        public Producer(string id, ProducerSetting setting)
        {
            if (id == null)
            {
                throw new ArgumentNullException("id");
            }
            Id = id;
            Setting = setting ?? new ProducerSetting();

            _lockObject = new object();
            _taskIds = new List<int>();
            _topicQueueIdsDict = new ConcurrentDictionary<string, IList<int>>();
            _remotingClient = new SocketRemotingClient(null, Setting.BrokerProducerIPEndPoint, this);
            _scheduleService = ObjectContainer.Resolve<IScheduleService>();
            _binarySerializer = ObjectContainer.Resolve<IBinarySerializer>();
            _queueSelector = ObjectContainer.Resolve<IQueueSelector>();
            _logger = ObjectContainer.Resolve<ILoggerFactory>().Create(GetType().FullName);
        }

        public Producer Start()
        {
            _remotingClient.Start();
            _logger.InfoFormat("Started, producerId:{0}", Id);
            return this;
        }
        public Producer Shutdown()
        {
            _remotingClient.Shutdown();
            _logger.InfoFormat("Shutdown, producerId:{0}", Id);
            return this;
        }
        public SendResult Send(Message message, object arg)
        {
            var queueIds = GetTopicQueueIds(message.Topic);
            var queueId = _queueSelector.SelectQueueId(queueIds, message, arg);
            var remotingRequest = BuildSendMessageRequest(message, queueId);
            var remotingResponse = _remotingClient.InvokeSync(remotingRequest, Setting.SendMessageTimeoutMilliseconds);
            var response = _binarySerializer.Deserialize<SendMessageResponse>(remotingResponse.Body);
            return new SendResult(SendStatus.Success, response.MessageOffset, response.MessageQueue, response.QueueOffset);
        }
        public Task<SendResult> SendAsync(Message message, object arg)
        {
            var queueIds = GetTopicQueueIds(message.Topic);
            var queueId = _queueSelector.SelectQueueId(queueIds, message, arg);
            var remotingRequest = BuildSendMessageRequest(message, queueId);
            var taskCompletionSource = new TaskCompletionSource<SendResult>();
            _remotingClient.InvokeAsync(remotingRequest, Setting.SendMessageTimeoutMilliseconds).ContinueWith((requestTask) =>
            {
                if (requestTask.Exception != null && requestTask.Exception.InnerExceptions.Count > 0)
                {
                    if (requestTask.Exception.InnerExceptions.First() is RemotingTimeoutException)
                    {
                        taskCompletionSource.SetResult(new SendResult(SendStatus.Timeout, requestTask.Exception.InnerExceptions[0].Message));
                    }
                    else
                    {
                        taskCompletionSource.SetResult(new SendResult(SendStatus.Failed, requestTask.Exception.InnerExceptions[0].Message));
                    }
                    return;
                }

                var remotingResponse = requestTask.Result;
                if (remotingResponse != null)
                {
                    var response = _binarySerializer.Deserialize<SendMessageResponse>(remotingResponse.Body);
                    var result = new SendResult(SendStatus.Success, response.MessageOffset, response.MessageQueue, response.QueueOffset);
                    taskCompletionSource.SetResult(result);
                }
                else
                {
                    var errorMessage = "Unknown error occurred when sending message to broker.";
                    if (!requestTask.IsCompleted)
                    {
                        errorMessage = "Send message to broker timeout.";
                    }
                    else if (requestTask.IsFaulted)
                    {
                        errorMessage = requestTask.Exception.Message;
                    }
                    taskCompletionSource.SetResult(new SendResult(SendStatus.Failed, errorMessage));
                }
            });
            return taskCompletionSource.Task;
        }

        private IList<int> GetTopicQueueIds(string topic)
        {
            IList<int> queueIds;
            if (!_topicQueueIdsDict.TryGetValue(topic, out queueIds))
            {
                var queueIdsFromServer = GetTopicQueueIdsFromServer(topic).ToList();
                _topicQueueIdsDict[topic] = queueIdsFromServer;
                queueIds = queueIdsFromServer;
            }

            return queueIds;
        }
        private void RefreshTopicQueueCount()
        {
            foreach (var topic in _topicQueueIdsDict.Keys)
            {
                UpdateTopicQueues(topic);
            }
        }
        private void UpdateTopicQueues(string topic)
        {
            try
            {
                var topicQueueIdsFromServer = GetTopicQueueIdsFromServer(topic).ToList();
                IList<int> currentQueueIds;
                var topicQueueIdsOfLocal = _topicQueueIdsDict.TryGetValue(topic, out currentQueueIds) ? currentQueueIds : new List<int>();

                if (IsIntCollectionChanged(topicQueueIdsFromServer, topicQueueIdsOfLocal))
                {
                    _topicQueueIdsDict[topic] = topicQueueIdsFromServer;
                    _logger.DebugFormat("Queues of topic changed, producerId:{0}, topic:{1}, old queueIds:{2}, new queueIds:{3}}", Id, topic, string.Join(":", topicQueueIdsOfLocal), string.Join(":", topicQueueIdsFromServer));
                }
            }
            catch (Exception ex)
            {
                _logger.Error(string.Format("UpdateTopicQueues has exception, producerId:{0}, topic:{1}", Id, topic), ex);
            }
        }
        private IEnumerable<int> GetTopicQueueIdsFromServer(string topic)
        {
            var remotingRequest = new RemotingRequest((int)RequestCode.GetTopicQueueIdsForProducer, Encoding.UTF8.GetBytes(topic));
            var remotingResponse = _remotingClient.InvokeSync(remotingRequest, 30000);
            if (remotingResponse.Code == (int)ResponseCode.Success)
            {
                var queueIds = Encoding.UTF8.GetString(remotingResponse.Body);
                return queueIds.Split(new[] { "," }, StringSplitOptions.RemoveEmptyEntries).Select(x => int.Parse(x));
            }
            else
            {
                throw new Exception(string.Format("GetTopicQueueIds has exception, producerId:{0}, topic:{1}, remoting response code:{2}", Id, topic, remotingResponse.Code));
            }
        }
        private RemotingRequest BuildSendMessageRequest(Message message, int queueId)
        {
            var request = new SendMessageRequest { Message = message, QueueId = queueId };
            var data = MessageUtils.EncodeSendMessageRequest(request);
            return new RemotingRequest((int)RequestCode.SendMessage, data);
        }
        private void HandleRemotingClientConnectionChanged(bool isConnected)
        {
            if (isConnected)
            {
                StartBackgroundJobs();
            }
            else
            {
                StopBackgroundJobs();
            }
        }
        private void StartBackgroundJobs()
        {
            lock (_lockObject)
            {
                StopBackgroundJobsInternal();
                StartBackgroundJobsInternal();
            }
        }
        private void StopBackgroundJobs()
        {
            lock (_lockObject)
            {
                StopBackgroundJobsInternal();
            }
        }
        private void StartBackgroundJobsInternal()
        {
            _taskIds.Add(_scheduleService.ScheduleTask("Producer.RefreshTopicQueueCount", RefreshTopicQueueCount, Setting.UpdateTopicQueueCountInterval, Setting.UpdateTopicQueueCountInterval));
        }
        private void StopBackgroundJobsInternal()
        {
            foreach (var taskId in _taskIds)
            {
                _scheduleService.ShutdownTask(taskId);
            }
            Clear();
        }
        private void Clear()
        {
            _taskIds.Clear();
            _topicQueueIdsDict.Clear();
        }
        private bool IsIntCollectionChanged(IList<int> first, IList<int> second)
        {
            if (first.Count != second.Count)
            {
                return true;
            }
            for (var index = 0; index < first.Count; index++)
            {
                if (first[index] != second[index])
                {
                    return true;
                }
            }
            return false;
        }

        void ISocketClientEventListener.OnConnectionClosed(ECommon.TcpTransport.ITcpConnectionInfo connectionInfo, System.Net.Sockets.SocketError socketError)
        {
            StopBackgroundJobs();
        }
        void ISocketClientEventListener.OnConnectionEstablished(ECommon.TcpTransport.ITcpConnectionInfo connectionInfo)
        {
            StartBackgroundJobs();
        }
        void ISocketClientEventListener.OnConnectionFailed(ECommon.TcpTransport.ITcpConnectionInfo connectionInfo, System.Net.Sockets.SocketError socketError)
        {
        }
    }
}
