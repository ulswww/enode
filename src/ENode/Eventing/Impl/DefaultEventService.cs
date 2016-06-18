﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ECommon.IO;
using ECommon.Logging;
using ECommon.Scheduling;
using ECommon.Serializing;
using ENode.Commanding;
using ENode.Configurations;
using ENode.Domain;
using ENode.Infrastructure;

namespace ENode.Eventing.Impl
{
    public class DefaultEventService : IEventService
    {
        #region Private Variables

        private IProcessingCommandHandler _processingCommandHandler;
        private readonly ConcurrentDictionary<string, EventMailBox> _eventMailboxDict;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IScheduleService _scheduleService;
        private readonly ITypeNameProvider _typeNameProvider;
        private readonly IMemoryCache _memoryCache;
        private readonly IAggregateRootFactory _aggregateRootFactory;
        private readonly IAggregateStorage _aggregateStorage;
        private readonly IEventStore _eventStore;
        private readonly IMessagePublisher<DomainEventStreamMessage> _domainEventPublisher;
        private readonly IOHelper _ioHelper;
        private readonly ILogger _logger;
        private readonly int _batchSize;

        #endregion

        #region Constructors

        public DefaultEventService(
            IJsonSerializer jsonSerializer,
            IScheduleService scheduleService,
            ITypeNameProvider typeNameProvider,
            IMemoryCache memoryCache,
            IAggregateRootFactory aggregateRootFactory,
            IAggregateStorage aggregateStorage,
            IEventStore eventStore,
            IMessagePublisher<DomainEventStreamMessage> domainEventPublisher,
            IOHelper ioHelper,
            ILoggerFactory loggerFactory)
        {
            _eventMailboxDict = new ConcurrentDictionary<string, EventMailBox>();
            _ioHelper = ioHelper;
            _jsonSerializer = jsonSerializer;
            _scheduleService = scheduleService;
            _typeNameProvider = typeNameProvider;
            _memoryCache = memoryCache;
            _aggregateRootFactory = aggregateRootFactory;
            _aggregateStorage = aggregateStorage;
            _eventStore = eventStore;
            _domainEventPublisher = domainEventPublisher;
            _logger = loggerFactory.Create(GetType().FullName);
            _batchSize = ENodeConfiguration.Instance.Setting.EventMailBoxPersistenceMaxBatchSize;
        }

        #endregion

        #region Public Methods

        public void SetProcessingCommandHandler(IProcessingCommandHandler processingCommandHandler)
        {
            _processingCommandHandler = processingCommandHandler;
        }
        public void CommitDomainEventAsync(EventCommittingContext context)
        {
            var eventMailbox = _eventMailboxDict.GetOrAdd(context.AggregateRoot.UniqueId, x =>
            {
                return new EventMailBox(x, _batchSize, committingContexts =>
                {
                    if (committingContexts == null || committingContexts.Count == 0)
                    {
                        return;
                    }
                    if (_eventStore.SupportBatchAppendEvent)
                    {
                        BatchPersistEventAsync(committingContexts, 0);
                    }
                    else
                    {
                        PersistEventOneByOne(committingContexts);
                    }
                });
            });
            eventMailbox.EnqueueMessage(context);
            RefreshAggregateMemoryCache(context);
            context.ProcessingCommand.Mailbox.TryExecuteNextMessage();
        }
        public void PublishDomainEventAsync(ProcessingCommand processingCommand, DomainEventStream eventStream)
        {
            if (eventStream.Items == null || eventStream.Items.Count == 0)
            {
                eventStream.Items = processingCommand.Items;
            }
            var eventStreamMessage = new DomainEventStreamMessage(processingCommand.Message.Id, eventStream.AggregateRootId, eventStream.Version, eventStream.AggregateRootTypeName, eventStream.Events, eventStream.Items);
            PublishDomainEventAsync(processingCommand, eventStreamMessage, 0);
        }

        #endregion

        #region Private Methods

        private void BatchPersistEventAsync(IList<EventCommittingContext> committingContexts, int retryTimes)
        {
            _ioHelper.TryAsyncActionRecursively("BatchPersistEventAsync",
            () => _eventStore.BatchAppendAsync(committingContexts.Select(x => x.EventStream)),
            currentRetryTimes => BatchPersistEventAsync(committingContexts, currentRetryTimes),
            result =>
            {
                var eventMailBox = committingContexts.First().EventMailBox;
                var appendResult = result.Data;
                if (appendResult == EventAppendResult.Success)
                {
                    if (_logger.IsDebugEnabled)
                    {
                        _logger.DebugFormat("Batch persist event success, aggregateRootId: {0}, eventStreamCount: {1}", eventMailBox.AggregateRootId, committingContexts.Count);
                    }

                    Task.Factory.StartNew(x =>
                    {
                        var contextList = x as IList<EventCommittingContext>;
                        foreach (var context in contextList)
                        {
                            PublishDomainEventAsync(context.ProcessingCommand, context.EventStream);
                        }
                    }, committingContexts);

                    eventMailBox.RegisterForExecution(true);
                }
                else if (appendResult == EventAppendResult.DuplicateEvent)
                {
                    var context = committingContexts.First();
                    if (context.EventStream.Version == 1)
                    {
                        ConcatConetxts(committingContexts);
                        HandleFirstEventDuplicationAsync(context, 0);
                    }
                    else
                    {
                        _logger.WarnFormat("Batch persist event has concurrent version conflict, first eventStream: {0}, batchSize: {1}", context.EventStream, committingContexts.Count);
                        ResetCommandMailBoxConsumingOffset(context, context.ProcessingCommand.Sequence);
                    }
                }
                else if (appendResult == EventAppendResult.DuplicateCommand)
                {
                    PersistEventOneByOne(committingContexts);
                }
            },
            () => string.Format("[contextListCount:{0}]", committingContexts.Count),
            errorMessage =>
            {
                _logger.Fatal(string.Format("Batch persist event has unknown exception, the code should not be run to here, errorMessage: {0}", errorMessage));
            },
            retryTimes, true);
        }
        private void PersistEventOneByOne(IList<EventCommittingContext> contextList)
        {
            ConcatConetxts(contextList);
            PersistEventAsync(contextList[0], 0);
        }
        private void PersistEventAsync(EventCommittingContext context, int retryTimes)
        {
            _ioHelper.TryAsyncActionRecursively("PersistEventAsync",
            () => _eventStore.AppendAsync(context.EventStream),
            currentRetryTimes => PersistEventAsync(context, currentRetryTimes),
            result =>
            {
                if (result.Data == EventAppendResult.Success)
                {
                    if (_logger.IsDebugEnabled)
                    {
                        _logger.DebugFormat("Persist event success, {0}", context.EventStream);
                    }

                    Task.Factory.StartNew(x =>
                    {
                        var currentContext = x as EventCommittingContext;
                        PublishDomainEventAsync(currentContext.ProcessingCommand, currentContext.EventStream);
                    }, context);

                    TryProcessNextContext(context);
                }
                else if (result.Data == EventAppendResult.DuplicateEvent)
                {
                    //如果是当前事件的版本号为1，则认为是在创建重复的聚合根
                    if (context.EventStream.Version == 1)
                    {
                        HandleFirstEventDuplicationAsync(context, 0);
                    }
                    //如果事件的版本大于1，则认为是更新聚合根时遇到并发冲突了，则需要进行重试；
                    else
                    {
                        _logger.WarnFormat("Persist event has concurrent version conflict, eventStream: {0}", context.EventStream);
                        ResetCommandMailBoxConsumingOffset(context, context.ProcessingCommand.Sequence);
                    }
                }
                else if (result.Data == EventAppendResult.DuplicateCommand)
                {
                    _logger.WarnFormat("Persist event has duplicate command, eventStream: {0}", context.EventStream);
                    ResetCommandMailBoxConsumingOffset(context, context.ProcessingCommand.Sequence + 1);
                    TryToRepublishEventAsync(context, 0);
                    context.EventMailBox.RegisterForExecution(true);
                }
            },
            () => string.Format("[eventStream:{0}]", context.EventStream),
            errorMessage =>
            {
                _logger.Fatal(string.Format("Persist event has unknown exception, the code should not be run to here, errorMessage: {0}", errorMessage));
            },
            retryTimes, true);
        }
        private void ResetCommandMailBoxConsumingOffset(EventCommittingContext context, long consumeOffset)
        {
            var eventMailBox = context.EventMailBox;
            var processingCommand = context.ProcessingCommand;
            var commandMailBox = processingCommand.Mailbox;

            commandMailBox.StopHandlingMessage();
            UpdateAggregateMemoryCacheToLatestVersion(context.EventStream);
            commandMailBox.ResetConsumingOffset(consumeOffset);
            eventMailBox.Clear();
            eventMailBox.ExitHandlingMessage();
            commandMailBox.RestartHandlingMessage();
        }
        private void TryToRepublishEventAsync(EventCommittingContext context, int retryTimes)
        {
            var command = context.ProcessingCommand.Message;

            _ioHelper.TryAsyncActionRecursively("FindEventByCommandIdAsync",
            () => _eventStore.FindAsync(command.AggregateRootId, command.Id),
            currentRetryTimes => TryToRepublishEventAsync(context, currentRetryTimes),
            result =>
            {
                var existingEventStream = result.Data;
                if (existingEventStream != null)
                {
                    //这里，我们需要再重新做一遍发布事件这个操作；
                    //之所以要这样做是因为虽然该command产生的事件已经持久化成功，但并不表示事件已经发布出去了；
                    //因为有可能事件持久化成功了，但那时正好机器断电了，则发布事件都没有做；
                    PublishDomainEventAsync(context.ProcessingCommand, existingEventStream);
                }
                else
                {
                    //到这里，说明当前command想添加到eventStore中时，提示command重复，但是尝试从eventStore中取出该command时却找不到该command。
                    //出现这种情况，我们就无法再做后续处理了，这种错误理论上不会出现，除非eventStore的Add接口和Get接口出现读写不一致的情况；
                    //框架会记录错误日志，让开发者排查具体是什么问题。
                    var errorMessage = string.Format("Command exist in the event store, but we cannot find it from the event store, this should not be happen, and we cannot continue again. commandType:{0}, commandId:{1}, aggregateRootId:{2}",
                        command.GetType().Name,
                        command.Id,
                        command.AggregateRootId);
                    _logger.Fatal(errorMessage);
                }
            },
            () => string.Format("[aggregateRootId:{0}, commandId:{1}]", command.AggregateRootId, command.Id),
            errorMessage =>
            {
                _logger.Fatal(string.Format("Find event by commandId has unknown exception, the code should not be run to here, errorMessage: {0}", errorMessage));
            },
            retryTimes, true);
        }
        private void HandleFirstEventDuplicationAsync(EventCommittingContext context, int retryTimes)
        {
            var eventStream = context.EventStream;

            _ioHelper.TryAsyncActionRecursively("FindFirstEventByVersion",
            () => _eventStore.FindAsync(eventStream.AggregateRootId, 1),
            currentRetryTimes => HandleFirstEventDuplicationAsync(context, currentRetryTimes),
            result =>
            {
                var firstEventStream = result.Data;
                if (firstEventStream != null)
                {
                    //判断是否是同一个command，如果是，则再重新做一遍发布事件；
                    //之所以要这样做，是因为虽然该command产生的事件已经持久化成功，但并不表示事件也已经发布出去了；
                    //有可能事件持久化成功了，但那时正好机器断电了，则发布事件都没有做；
                    if (context.ProcessingCommand.Message.Id == firstEventStream.CommandId)
                    {
                        ResetCommandMailBoxConsumingOffset(context, context.ProcessingCommand.Sequence + 1);
                        PublishDomainEventAsync(context.ProcessingCommand, firstEventStream);
                    }
                    else
                    {
                        //如果不是同一个command，则认为是两个不同的command重复创建ID相同的聚合根，我们需要记录错误日志，然后通知当前command的处理完成；
                        var errorMessage = string.Format("Duplicate aggregate creation. current commandId:{0}, existing commandId:{1}, aggregateRootId:{2}, aggregateRootTypeName:{3}",
                            context.ProcessingCommand.Message.Id,
                            firstEventStream.CommandId,
                            firstEventStream.AggregateRootId,
                            firstEventStream.AggregateRootTypeName);
                        _logger.Error(errorMessage);
                        ResetCommandMailBoxConsumingOffset(context, context.ProcessingCommand.Sequence + 1);
                        CompleteCommand(context.ProcessingCommand, new CommandResult(CommandStatus.Failed, context.ProcessingCommand.Message.Id, eventStream.AggregateRootId, "Duplicate aggregate creation.", typeof(string).FullName));
                    }
                }
                else
                {
                    var errorMessage = string.Format("Duplicate aggregate creation, but we cannot find the existing eventstream from eventstore, this should not be happen, and we cannot continue again. commandId:{0}, aggregateRootId:{1}, aggregateRootTypeName:{2}",
                        eventStream.CommandId,
                        eventStream.AggregateRootId,
                        eventStream.AggregateRootTypeName);
                    _logger.Fatal(errorMessage);
                }
            },
            () => string.Format("[eventStream:{0}]", eventStream),
            errorMessage =>
            {
                _logger.Fatal(string.Format("Find the first version of event has unknown exception, the code should not be run to here, errorMessage: {0}", errorMessage));
            },
            retryTimes, true);
        }
        private void RefreshAggregateMemoryCache(EventCommittingContext context)
        {
            try
            {
                context.AggregateRoot.AcceptChanges(context.EventStream.Version);
                _memoryCache.Set(context.AggregateRoot);
            }
            catch (Exception ex)
            {
                _logger.Error(string.Format("Refresh memory cache failed for event stream:{0}", context.EventStream), ex);
            }
        }
        private void UpdateAggregateMemoryCacheToLatestVersion(DomainEventStream eventStream)
        {
            try
            {
                _memoryCache.RefreshAggregateFromEventStore(eventStream.AggregateRootTypeName, eventStream.AggregateRootId);
            }
            catch (Exception ex)
            {
                _logger.Error(string.Format("Try to refresh aggregate in-memory from event store failed, eventStream: {0}", eventStream), ex);
            }
        }
        private void TryProcessNextContext(EventCommittingContext currentContext)
        {
            if (currentContext.Next != null)
            {
                PersistEventAsync(currentContext.Next, 0);
            }
            else
            {
                currentContext.EventMailBox.RegisterForExecution(true);
            }
        }
        private void PublishDomainEventAsync(ProcessingCommand processingCommand, DomainEventStreamMessage eventStream, int retryTimes)
        {
            _ioHelper.TryAsyncActionRecursively("PublishEventAsync",
            () => _domainEventPublisher.PublishAsync(eventStream),
            currentRetryTimes => PublishDomainEventAsync(processingCommand, eventStream, currentRetryTimes),
            result =>
            {
                if (_logger.IsDebugEnabled)
                {
                    _logger.DebugFormat("Publish event success, {0}", eventStream);
                }
                var commandHandleResult = processingCommand.CommandExecuteContext.GetResult();
                CompleteCommand(processingCommand, new CommandResult(CommandStatus.Success, processingCommand.Message.Id, eventStream.AggregateRootId, commandHandleResult, typeof(string).FullName));
            },
            () => string.Format("[eventStream:{0}]", eventStream),
            errorMessage =>
            {
                _logger.Fatal(string.Format("Publish event has unknown exception, the code should not be run to here, errorMessage: {0}", errorMessage));
            },
            retryTimes, true);
        }
        private void ConcatConetxts(IList<EventCommittingContext> contextList)
        {
            for (var i = 0; i < contextList.Count - 1; i++)
            {
                var currentContext = contextList[i];
                var nextContext = contextList[i + 1];
                currentContext.Next = nextContext;
            }
        }
        private void CompleteCommand(ProcessingCommand processingCommand, CommandResult commandResult)
        {
            processingCommand.Mailbox.CompleteMessage(processingCommand, commandResult);
            _logger.InfoFormat("Complete command, aggregateId: {0}", processingCommand.Message.AggregateRootId);
        }

        #endregion
    }
}
