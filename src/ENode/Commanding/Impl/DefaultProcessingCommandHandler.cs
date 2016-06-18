﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ECommon.IO;
using ECommon.Logging;
using ECommon.Serializing;
using ENode.Domain;
using ENode.Eventing;
using ENode.Infrastructure;

namespace ENode.Commanding.Impl
{
    public class DefaultProcessingCommandHandler : IProcessingCommandHandler
    {
        #region Private Variables

        private readonly IJsonSerializer _jsonSerializer;
        private readonly ICommandStore _commandStore;
        private readonly IEventStore _eventStore;
        private readonly ICommandHandlerProvider _commandHandlerProvider;
        private readonly ICommandAsyncHandlerProvider _commandAsyncHandlerProvider;
        private readonly ITypeNameProvider _typeNameProvider;
        private readonly IEventService _eventService;
        private readonly IMessagePublisher<IApplicationMessage> _messagePublisher;
        private readonly IMessagePublisher<IPublishableException> _exceptionPublisher;
        private readonly IMemoryCache _memoryCache;
        private readonly IOHelper _ioHelper;
        private readonly ILogger _logger;

        #endregion

        #region Constructors

        public DefaultProcessingCommandHandler(
            IJsonSerializer jsonSerializer,
            ICommandStore commandStore,
            IEventStore eventStore,
            ICommandHandlerProvider commandHandlerProvider,
            ICommandAsyncHandlerProvider commandAsyncHandlerProvider,
            ITypeNameProvider typeNameProvider,
            IEventService eventService,
            IMessagePublisher<IApplicationMessage> messagePublisher,
            IMessagePublisher<IPublishableException> exceptionPublisher,
            IMemoryCache memoryCache,
            IOHelper ioHelper,
            ILoggerFactory loggerFactory)
        {
            _jsonSerializer = jsonSerializer;
            _commandStore = commandStore;
            _eventStore = eventStore;
            _commandHandlerProvider = commandHandlerProvider;
            _commandAsyncHandlerProvider = commandAsyncHandlerProvider;
            _typeNameProvider = typeNameProvider;
            _eventService = eventService;
            _messagePublisher = messagePublisher;
            _exceptionPublisher = exceptionPublisher;
            _memoryCache = memoryCache;
            _ioHelper = ioHelper;
            _logger = loggerFactory.Create(GetType().FullName);
            _eventService.SetProcessingCommandHandler(this);
        }

        #endregion

        #region Public Methods

        public void HandleAsync(ProcessingCommand processingCommand)
        {
            var command = processingCommand.Message;

            if (string.IsNullOrEmpty(command.AggregateRootId))
            {
                var errorMessage = string.Format("The aggregateRootId of command cannot be null or empty. commandType:{0}, commandId:{1}", command.GetType().Name, command.Id);
                _logger.Error(errorMessage);
                CompleteCommand(processingCommand, CommandStatus.Failed, typeof(string).FullName, errorMessage);
                return;
            }

            ICommandHandlerProxy commandHandler;
            var findResult = GetCommandHandler(processingCommand, commandType => _commandHandlerProvider.GetHandlers(commandType), out commandHandler);
            if (findResult == HandlerFindResult.Found)
            {
                HandleCommand(processingCommand, commandHandler);
            }
            else if (findResult == HandlerFindResult.TooManyHandlerData)
            {
                _logger.ErrorFormat("Found more than one command handler data, commandType:{0}, commandId:{1}", command.GetType().FullName, command.Id);
                CompleteCommand(processingCommand, CommandStatus.Failed, typeof(string).FullName, "More than one command handler data found.");
            }
            else if (findResult == HandlerFindResult.TooManyHandler)
            {
                _logger.ErrorFormat("Found more than one command handler, commandType:{0}, commandId:{1}", command.GetType().FullName, command.Id);
                CompleteCommand(processingCommand, CommandStatus.Failed, typeof(string).FullName, "More than one command handler found.");
            }
            else if (findResult == HandlerFindResult.NotFound)
            {
                ICommandAsyncHandlerProxy commandAsyncHandler;
                findResult = GetCommandHandler(processingCommand, commandType => _commandAsyncHandlerProvider.GetHandlers(commandType), out commandAsyncHandler);
                if (findResult == HandlerFindResult.Found)
                {
                    HandleCommand(processingCommand, commandAsyncHandler);
                }
                else if (findResult == HandlerFindResult.TooManyHandlerData)
                {
                    _logger.ErrorFormat("Found more than one command async handler data, commandType:{0}, commandId:{1}", command.GetType().FullName, command.Id);
                    CompleteCommand(processingCommand, CommandStatus.Failed, typeof(string).FullName, "More than one command async handler data found.");
                }
                else if (findResult == HandlerFindResult.TooManyHandler)
                {
                    _logger.ErrorFormat("Found more than one command async handler, commandType:{0}, commandId:{1}", command.GetType().FullName, command.Id);
                    CompleteCommand(processingCommand, CommandStatus.Failed, typeof(string).FullName, "More than one command async handler found.");
                }
                else if (findResult == HandlerFindResult.NotFound)
                {
                    var errorMessage = string.Format("No command handler found of command. commandType:{0}, commandId:{1}", command.GetType().Name, command.Id);
                    _logger.Error(errorMessage);
                    CompleteCommand(processingCommand, CommandStatus.Failed, typeof(string).FullName, errorMessage);
                }
            }
        }

        #endregion

        #region Command Handler Helper Methods

        private void HandleCommand(ProcessingCommand processingCommand, ICommandHandlerProxy commandHandler)
        {
            var command = processingCommand.Message;

            processingCommand.CommandExecuteContext.Clear();

            //调用command handler执行当前command
            var handleSuccess = false;
            try
            {
                commandHandler.Handle(processingCommand.CommandExecuteContext, command);
                if (_logger.IsDebugEnabled)
                {
                    _logger.DebugFormat("Handle command success. handlerType:{0}, commandType:{1}, commandId:{2}, aggregateRootId:{3}",
                        commandHandler.GetInnerObject().GetType().Name,
                        command.GetType().Name,
                        command.Id,
                        command.AggregateRootId);
                }
                handleSuccess = true;
            }
            catch (Exception ex)
            {
                HandleExceptionAsync(processingCommand, commandHandler, ex, 0);
                return;
            }

            //如果command执行成功，则提交执行后的结果
            if (handleSuccess)
            {
                try
                {
                    CommitAggregateChanges(processingCommand);
                }
                catch (Exception ex)
                {
                    LogCommandExecuteException(processingCommand, commandHandler, ex);
                    CompleteCommand(processingCommand, CommandStatus.Failed, ex.GetType().Name, "Unknown exception caught when committing changes of command.");
                }
            }
        }
        private void CommitAggregateChanges(ProcessingCommand processingCommand)
        {
            var command = processingCommand.Message;
            var context = processingCommand.CommandExecuteContext;
            var trackedAggregateRoots = context.GetTrackedAggregateRoots();
            var dirtyAggregateRootCount = 0;
            var dirtyAggregateRoot = default(IAggregateRoot);
            var changedEvents = default(IEnumerable<IDomainEvent>);

            foreach (var aggregateRoot in trackedAggregateRoots)
            {
                var events = aggregateRoot.GetChanges();
                if (events.Any())
                {
                    dirtyAggregateRootCount++;
                    if (dirtyAggregateRootCount > 1)
                    {
                        var errorMessage = string.Format("Detected more than one aggregate created or modified by command. commandType:{0}, commandId:{1}",
                            command.GetType().Name,
                            command.Id);
                        _logger.ErrorFormat(errorMessage);
                        CompleteCommand(processingCommand, CommandStatus.Failed, typeof(string).FullName, errorMessage);
                        return;
                    }
                    dirtyAggregateRoot = aggregateRoot;
                    changedEvents = events;
                }
            }

            //如果当前command没有对任何聚合根做修改，则认为当前command已经处理结束，返回command的结果为NothingChanged
            if (dirtyAggregateRootCount == 0 || changedEvents == null || changedEvents.Count() == 0)
            {
                CompleteCommand(processingCommand, CommandStatus.NothingChanged, typeof(string).FullName, context.GetResult());
                return;
            }

            //构造出一个事件流对象
            var eventStream = BuildDomainEventStream(dirtyAggregateRoot, changedEvents, processingCommand);

            //将事件流提交到EventStore
            _eventService.CommitDomainEventAsync(new EventCommittingContext(dirtyAggregateRoot, eventStream, processingCommand));
        }
        private DomainEventStream BuildDomainEventStream(IAggregateRoot aggregateRoot, IEnumerable<IDomainEvent> changedEvents, ProcessingCommand processingCommand)
        {
            var commandResult = processingCommand.CommandExecuteContext.GetResult();
            if (commandResult != null)
            {
                processingCommand.Items["CommandResult"] = commandResult;
            }
            return new DomainEventStream(
                processingCommand.Message.Id,
                aggregateRoot.UniqueId,
                _typeNameProvider.GetTypeName(aggregateRoot.GetType()),
                aggregateRoot.Version + 1,
                DateTime.Now,
                changedEvents,
                processingCommand.Items);
        }
        private void HandleExceptionAsync(ProcessingCommand processingCommand, ICommandHandlerProxy commandHandler, Exception exception, int retryTimes)
        {
            var command = processingCommand.Message;

            _ioHelper.TryAsyncActionRecursively("FindEventByCommandIdAsync",
            () => _eventStore.FindAsync(command.AggregateRootId, command.Id),
            currentRetryTimes => HandleExceptionAsync(processingCommand, commandHandler, exception, currentRetryTimes),
            result =>
            {
                var existingEventStream = result.Data;
                if (existingEventStream != null)
                {
                    //这里，我们需要再重新做一遍发布事件这个操作；
                    //之所以要这样做是因为虽然该command产生的事件已经持久化成功，但并不表示事件已经发布出去了；
                    //因为有可能事件持久化成功了，但那时正好机器断电了，则发布事件就没有做；
                    _eventService.PublishDomainEventAsync(processingCommand, existingEventStream);
                }
                else
                {
                    //到这里，说明当前command执行遇到异常，然后当前command之前也没执行过，是第一次被执行。
                    //那就判断当前异常是否是需要被发布出去的异常，如果是，则发布该异常给所有消费者；
                    //否则，就记录错误日志，然后认为该command处理失败即可；
                    var publishableException = exception as IPublishableException;
                    if (publishableException != null)
                    {
                        PublishExceptionAsync(processingCommand, publishableException, 0);
                    }
                    else
                    {
                        LogCommandExecuteException(processingCommand, commandHandler, exception);
                        CompleteCommand(processingCommand, CommandStatus.Failed, exception.GetType().Name, exception.Message);
                    }
                }
            },
            () => string.Format("[commandId:{0}]", command.Id),
            errorMessage =>
            {
                _logger.Fatal(string.Format("Find event by commandId has unknown exception, the code should not be run to here, errorMessage: {0}", errorMessage));
            },
            retryTimes, true);
        }
        private void PublishExceptionAsync(ProcessingCommand processingCommand, IPublishableException exception, int retryTimes)
        {
            _ioHelper.TryAsyncActionRecursively("PublishExceptionAsync",
            () => _exceptionPublisher.PublishAsync(exception),
            currentRetryTimes => PublishExceptionAsync(processingCommand, exception, currentRetryTimes),
            result =>
            {
                CompleteCommand(processingCommand, CommandStatus.Failed, exception.GetType().Name, (exception as Exception).Message);
            },
            () =>
            {
                var serializableInfo = new Dictionary<string, string>();
                exception.SerializeTo(serializableInfo);
                var exceptionInfo = string.Join(",", serializableInfo.Select(x => string.Format("{0}:{1}", x.Key, x.Value)));
                return string.Format("[commandId:{0}, exceptionInfo:{1}]", processingCommand.Message.Id, exceptionInfo);
            },
            errorMessage =>
            {
                _logger.Fatal(string.Format("Publish event has unknown exception, the code should not be run to here, errorMessage: {0}", errorMessage));
            },
            retryTimes, true);
        }
        private void LogCommandExecuteException(ProcessingCommand processingCommand, ICommandHandlerProxy commandHandler, Exception exception)
        {
            var command = processingCommand.Message;
            var errorMessage = string.Format("{0} raised when {1} handling {2}. commandId:{3}, aggregateRootId:{4}",
                exception.GetType().Name,
                commandHandler.GetInnerObject().GetType().Name,
                command.GetType().Name,
                command.Id,
                command.AggregateRootId);
            _logger.Error(errorMessage, exception);
        }

        #endregion

        #region Command Async Handler Help Methods

        private void HandleCommand(ProcessingCommand processingCommand, ICommandAsyncHandlerProxy commandHandler)
        {
            var realHandler = commandHandler.GetInnerObject() as ICommandAsyncHandler;
            if (realHandler.CheckCommandHandledFirst)
            {
                ProcessCommand(processingCommand, commandHandler, 0);
            }
            else
            {
                HandleCommandAsync(processingCommand, commandHandler, 0);
            }
        }
        private void ProcessCommand(ProcessingCommand processingCommand, ICommandAsyncHandlerProxy commandAsyncHandler, int retryTimes)
        {
            var command = processingCommand.Message;

            _ioHelper.TryAsyncActionRecursively("GetCommandAsync",
            () => _commandStore.GetAsync(command.Id),
            currentRetryTimes => ProcessCommand(processingCommand, commandAsyncHandler, currentRetryTimes),
            result =>
            {
                var existingHandledCommand = result.Data;
                if (existingHandledCommand != null)
                {
                    if (existingHandledCommand.Message != null)
                    {
                        PublishMessageAsync(processingCommand, existingHandledCommand.Message, 0);
                    }
                    else
                    {
                        CompleteCommand(processingCommand, CommandStatus.Success, null, null);
                    }
                    return;
                }
                HandleCommandAsync(processingCommand, commandAsyncHandler, 0);
            },
            () => string.Format("[commandId:{0},commandType:{1}]", command.Id, command.GetType().Name),
            errorMessage =>
            {
                _logger.Fatal(string.Format("Get command by id has unknown exception, the code should not be run to here, errorMessage: {0}", errorMessage));
            },
            retryTimes, true);
        }
        private void HandleCommandAsync(ProcessingCommand processingCommand, ICommandAsyncHandlerProxy commandHandler, int retryTimes)
        {
            var command = processingCommand.Message;

            _ioHelper.TryAsyncActionRecursively("HandleCommandAsync",
            () =>
            {
                try
                {
                    var asyncResult = commandHandler.HandleAsync(command);
                    if (_logger.IsDebugEnabled)
                    {
                        _logger.DebugFormat("Handle command async success. handlerType:{0}, commandType:{1}, commandId:{2}, aggregateRootId:{3}",
                            commandHandler.GetInnerObject().GetType().Name,
                            command.GetType().Name,
                            command.Id,
                            command.AggregateRootId);
                    }
                    return asyncResult;
                }
                catch (IOException ex)
                {
                    _logger.Error(string.Format("Handle command async has io exception. handlerType:{0}, commandType:{1}, commandId:{2}, aggregateRootId:{3}",
                        commandHandler.GetInnerObject().GetType().Name,
                        command.GetType().Name,
                        command.Id,
                        command.AggregateRootId), ex);
                    return Task.FromResult(new AsyncTaskResult<IApplicationMessage>(AsyncTaskStatus.IOException, ex.Message));
                }
                catch (Exception ex)
                {
                    _logger.Error(string.Format("Handle command async has unknown exception. handlerType:{0}, commandType:{1}, commandId:{2}, aggregateRootId:{3}",
                        commandHandler.GetInnerObject().GetType().Name,
                        command.GetType().Name,
                        command.Id,
                        command.AggregateRootId), ex);
                    return Task.FromResult(new AsyncTaskResult<IApplicationMessage>(AsyncTaskStatus.Failed, ex.Message));
                }
            },
            currentRetryTimes => HandleCommandAsync(processingCommand, commandHandler, currentRetryTimes),
            result =>
            {
                CommitChangesAsync(processingCommand, true, result.Data, null, 0);
            },
            () => string.Format("[command:[id:{0},type:{1}],handlerType:{2}]", command.Id, command.GetType().Name, commandHandler.GetInnerObject().GetType().Name),
            errorMessage =>
            {
                CommitChangesAsync(processingCommand, false, null, errorMessage, 0);
            },
            retryTimes);
        }
        private void CommitChangesAsync(ProcessingCommand processingCommand, bool success, IApplicationMessage message, string errorMessage, int retryTimes)
        {
            var command = processingCommand.Message;
            var handledCommand = new HandledCommand(command.Id, command.AggregateRootId, message);

            _ioHelper.TryAsyncActionRecursively("AddCommandAsync",
            () => _commandStore.AddAsync(handledCommand),
            currentRetryTimes => CommitChangesAsync(processingCommand, success, message, errorMessage, currentRetryTimes),
            result =>
            {
                var commandAddResult = result.Data;
                if (commandAddResult == CommandAddResult.Success)
                {
                    if (success)
                    {
                        if (message != null)
                        {
                            PublishMessageAsync(processingCommand, message, 0);
                        }
                        else
                        {
                            CompleteCommand(processingCommand, CommandStatus.Success, null, null);
                        }
                    }
                    else
                    {
                        CompleteCommand(processingCommand, CommandStatus.Failed, typeof(string).FullName, errorMessage);
                    }
                }
                else if (commandAddResult == CommandAddResult.DuplicateCommand)
                {
                    HandleDuplicatedCommandAsync(processingCommand, 0);
                }
            },
            () => string.Format("[handledCommand:{0}]", handledCommand),
            error =>
            {
                _logger.Fatal(string.Format("Add command has unknown exception, the code should not be run to here, errorMessage: {0}", error));
            },
            retryTimes, true);
        }
        private void PublishMessageAsync(ProcessingCommand processingCommand, IApplicationMessage message, int retryTimes)
        {
            var command = processingCommand.Message;

            _ioHelper.TryAsyncActionRecursively("PublishApplicationMessageAsync",
            () => _messagePublisher.PublishAsync(message),
            currentRetryTimes => PublishMessageAsync(processingCommand, message, currentRetryTimes),
            result =>
            {
                CompleteCommand(processingCommand, CommandStatus.Success, message.GetTypeName(), _jsonSerializer.Serialize(message));
            },
            () => string.Format("[application message:[id:{0},type:{1}],command:[id:{2},type:{3}]]", message.Id, message.GetType().Name, command.Id, command.GetType().Name),
            errorMessage =>
            {
                _logger.Fatal(string.Format("Publish application message has unknown exception, the code should not be run to here, errorMessage: {0}", errorMessage));
            },
            retryTimes, true);
        }
        private void HandleDuplicatedCommandAsync(ProcessingCommand processingCommand, int retryTimes)
        {
            var command = processingCommand.Message;

            _ioHelper.TryAsyncActionRecursively("GetCommandAsync",
            () => _commandStore.GetAsync(command.Id),
            currentRetryTimes => HandleDuplicatedCommandAsync(processingCommand, currentRetryTimes),
            result =>
            {
                var existingHandledCommand = result.Data;
                if (existingHandledCommand != null)
                {
                    if (existingHandledCommand.Message != null)
                    {
                        PublishMessageAsync(processingCommand, existingHandledCommand.Message, 0);
                    }
                    else
                    {
                        CompleteCommand(processingCommand, CommandStatus.Success, null, null);
                    }
                }
                else
                {
                    //到这里，说明当前command想添加到commandStore中时，提示command重复，但是尝试从commandStore中取出该command时却找不到该command。
                    //出现这种情况，我们就无法再做后续处理了，这种错误理论上不会出现，除非commandStore的Add接口和Get接口出现读写不一致的情况；
                    //我们记录错误日志，然后认为当前command已被处理为失败。
                    var errorMessage = string.Format("Command exist in the command store, but we cannot get it from the command store. commandType:{0}, commandId:{1}, aggregateRootId:{2}",
                        command.GetType().Name,
                        command.Id,
                        command.AggregateRootId);
                    _logger.Error(errorMessage);
                    CompleteCommand(processingCommand, CommandStatus.Failed, null, errorMessage);
                }
            },
            () => string.Format("[command:[id:{0},type:{1}]", command.Id, command.GetType().Name),
            errorMessage =>
            {
                _logger.Fatal(string.Format("Get command by id has unknown exception, the code should not be run to here, errorMessage: {0}", errorMessage));
            },
            retryTimes, true);
        }

        #endregion

        private HandlerFindResult GetCommandHandler<T>(ProcessingCommand processingCommand, Func<Type, IEnumerable<MessageHandlerData<T>>> getHandlersFunc, out T handlerProxy) where T : class, IObjectProxy
        {
            handlerProxy = null;

            var command = processingCommand.Message;
            var handlerDataList = getHandlersFunc(command.GetType());

            if (handlerDataList == null || handlerDataList.Count() == 0)
            {
                return HandlerFindResult.NotFound;
            }
            else if (handlerDataList.Count() > 1)
            {
                return HandlerFindResult.TooManyHandlerData;
            }

            var handlerData = handlerDataList.Single();
            if (handlerData.ListHandlers == null || handlerData.ListHandlers.Count() == 0)
            {
                return HandlerFindResult.NotFound;
            }
            else if (handlerData.ListHandlers.Count() > 1)
            {
                return HandlerFindResult.TooManyHandler;
            }

            handlerProxy = handlerData.ListHandlers.Single() as T;

            return HandlerFindResult.Found;
        }
        private void CompleteCommand(ProcessingCommand processingCommand, CommandStatus commandStatus, string resultType, string result)
        {
            var commandResult = new CommandResult(commandStatus, processingCommand.Message.Id, processingCommand.Message.AggregateRootId, result, resultType);
            processingCommand.Mailbox.CompleteMessage(processingCommand, commandResult);
            processingCommand.Mailbox.TryExecuteNextMessage();
        }
        private enum HandlerFindResult
        {
            NotFound,
            Found,
            TooManyHandlerData,
            TooManyHandler
        }
    }
}
