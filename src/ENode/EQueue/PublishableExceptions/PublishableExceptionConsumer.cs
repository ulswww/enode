﻿using System.Runtime.Serialization;
using System.Text;
using ECommon.Components;
using ECommon.Serializing;
using ENode.Infrastructure;
using EQueue.Clients.Consumers;
using EQueue.Protocols;
using IQueueMessageHandler = EQueue.Clients.Consumers.IMessageHandler;

namespace ENode.EQueue
{
    public class PublishableExceptionConsumer : IQueueMessageHandler
    {
        private const string DefaultExceptionConsumerGroup = "ExceptionConsumerGroup";
        private readonly Consumer _consumer;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly ITypeNameProvider _typeNameProvider;
        private readonly IMessageProcessor<ProcessingPublishableExceptionMessage, IPublishableException, bool> _publishableExceptionProcessor;

        public Consumer Consumer { get { return _consumer; } }

        public PublishableExceptionConsumer(string groupName = null, ConsumerSetting setting = null)
        {
            _consumer = new Consumer(groupName ?? DefaultExceptionConsumerGroup, setting ?? new ConsumerSetting
            {
                MessageHandleMode = MessageHandleMode.Sequential,
                ConsumeFromWhere = ConsumeFromWhere.FirstOffset
            });
            _jsonSerializer = ObjectContainer.Resolve<IJsonSerializer>();
            _publishableExceptionProcessor = ObjectContainer.Resolve<IMessageProcessor<ProcessingPublishableExceptionMessage, IPublishableException, bool>>();
            _typeNameProvider = ObjectContainer.Resolve<ITypeNameProvider>();
        }

        public PublishableExceptionConsumer Start()
        {
            _consumer.SetMessageHandler(this).Start();
            return this;
        }
        public PublishableExceptionConsumer Subscribe(string topic)
        {
            _consumer.Subscribe(topic);
            return this;
        }
        public PublishableExceptionConsumer Shutdown()
        {
            _consumer.Shutdown();
            return this;
        }

        void IQueueMessageHandler.Handle(QueueMessage queueMessage, IMessageContext context)
        {
            var exceptionMessage = _jsonSerializer.Deserialize<PublishableExceptionMessage>(Encoding.UTF8.GetString(queueMessage.Body));
            var exceptionType = _typeNameProvider.GetType(queueMessage.Tag);
            var exception = FormatterServices.GetUninitializedObject(exceptionType) as IPublishableException;
            exception.Id = exceptionMessage.UniqueId;
            exception.Timestamp = exceptionMessage.Timestamp;
            exception.RestoreFrom(exceptionMessage.SerializableInfo);
            var sequenceMessage = exception as ISequenceMessage;
            if (sequenceMessage != null)
            {
                sequenceMessage.AggregateRootTypeName = exceptionMessage.AggregateRootTypeName;
                sequenceMessage.AggregateRootStringId = exceptionMessage.AggregateRootId;
            }
            var processContext = new EQueueProcessContext(queueMessage, context);
            var processingMessage = new ProcessingPublishableExceptionMessage(exception, processContext);
            _publishableExceptionProcessor.Process(processingMessage);
        }
    }
}
