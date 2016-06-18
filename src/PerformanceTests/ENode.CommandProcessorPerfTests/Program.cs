﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ECommon.Components;
using ECommon.Configurations;
using ECommon.Logging;
using ECommon.Utilities;
using ENode.Commanding;
using ENode.Configurations;
using ENode.Domain;
using ENode.Eventing;
using NoteSample.Commands;
using ECommonConfiguration = ECommon.Configurations.Configuration;

namespace ENode.CommandProcessorPerfTests
{
    class Program
    {
        static ENodeConfiguration _configuration;
        static ManualResetEvent _waitHandle;
        static ILogger _logger;
        static Stopwatch _watch;
        static IRepository _repository;
        static ICommandProcessor _commandProcessor;
        static IEventService _eventService;
        static string _connectionString;
        static int _eventTableCount;
        static int _commandCount;
        static int _executedCount;
        static int _totalCommandCount;
        static bool _isUpdating;

        static void Main(string[] args)
        {
            _connectionString = ConfigurationManager.AppSettings["connectionString"];
            _commandCount = int.Parse(ConfigurationManager.AppSettings["count"]);
            _eventTableCount = int.Parse(ConfigurationManager.AppSettings["eventTableCount"]);

            InitializeENodeFramework();

            var createCommands = new List<ProcessingCommand>();
            var updateCommandsList = new List<List<ProcessingCommand>>();

            for (var i = 0; i < _eventTableCount; i++)
            {
                createCommands.Add(new ProcessingCommand(new CreateNoteCommand
                {
                    AggregateRootId = GenerateAggregateRootId(_eventTableCount, i),
                    Title = "Sample Note"
                }, new CommandExecuteContext(_commandCount), new Dictionary<string, string>()));
            }
            foreach (var createCommand in createCommands)
            {
                var updateCommands = new List<ProcessingCommand>();
                for (var i = 0; i < _commandCount; i++)
                {
                    updateCommands.Add(new ProcessingCommand(new ChangeNoteTitleCommand
                    {
                        AggregateRootId = createCommand.Message.AggregateRootId,
                        Title = "Changed Note Title"
                    }, new CommandExecuteContext(_commandCount), new Dictionary<string, string>()));
                }
                updateCommandsList.Add(updateCommands);
            }

            _totalCommandCount = createCommands.Count;
            _waitHandle = new ManualResetEvent(false);
            foreach (var createCommand in createCommands)
            {
                _commandProcessor.Process(createCommand);
            }
            _waitHandle.WaitOne();

            _isUpdating = true;
            _executedCount = 0;
            _totalCommandCount = updateCommandsList.Sum(x => x.Count);
            _waitHandle = new ManualResetEvent(false);
            _watch = Stopwatch.StartNew();
            Console.WriteLine("");
            Console.WriteLine("--Start to process aggregate commands, total count: {0}.", _totalCommandCount);

            foreach (var updateCommands in updateCommandsList)
            {
                Task.Factory.StartNew(() =>
                {
                    foreach (var updateCommand in updateCommands)
                    {
                        _commandProcessor.Process(updateCommand);
                    }
                });
            }

            _waitHandle.WaitOne();
            Console.WriteLine("--Commands process completed, throughput: {0}/s", _totalCommandCount * 1000 / _watch.ElapsedMilliseconds);

            Console.ReadLine();
        }

        static string GenerateAggregateRootId(int tableCount, int expectIndex)
        {
            var aggregateRootId = ObjectId.GenerateNewStringId();
            var index = GetIndex(aggregateRootId, tableCount);
            while (index != expectIndex)
            {
                aggregateRootId = ObjectId.GenerateNewStringId();
                index = GetIndex(aggregateRootId, tableCount);
            }
            return aggregateRootId;
        }
        static int GetIndex(string aggregateRootId, int tableCount)
        {
            int hash = 23;
            foreach (char c in aggregateRootId)
            {
                hash = (hash << 5) - hash + c;
            }
            if (hash < 0)
            {
                hash = Math.Abs(hash);
            }
            return hash % tableCount;
        }
        static void InitializeENodeFramework()
        {
            var assemblies = new[]
            {
                Assembly.Load("NoteSample.Domain"),
                Assembly.Load("NoteSample.Commands"),
                Assembly.Load("NoteSample.CommandHandlers"),
                Assembly.GetExecutingAssembly()
            };
            var setting = new ConfigurationSetting(_connectionString);

            setting.DefaultDBConfigurationSetting.EventTableCount = _eventTableCount;

            _configuration = ECommonConfiguration
                .Create()
                .UseAutofac()
                .RegisterCommonComponents()
                .UseLog4Net()
                .UseJsonNet()
                .RegisterUnhandledExceptionHandler()
                .CreateENode(setting)
                .RegisterENodeComponents()
                .UseSqlServerEventStore()
                .RegisterBusinessComponents(assemblies)
                .InitializeBusinessAssemblies(assemblies);
            _eventService = ObjectContainer.Resolve<IEventService>();

            _logger = ObjectContainer.Resolve<ILoggerFactory>().Create("main");
            _repository = ObjectContainer.Resolve<IRepository>();
            _commandProcessor = ObjectContainer.Resolve<ICommandProcessor>();

            Console.WriteLine("ENode started...");
        }

        class CommandExecuteContext : ICommandExecuteContext
        {
            private readonly ConcurrentDictionary<string, IAggregateRoot> _aggregateRoots;
            private readonly int _printSize;
            private string _result;

            public CommandExecuteContext(int commandCount)
            {
                _aggregateRoots = new ConcurrentDictionary<string, IAggregateRoot>();
                _printSize = commandCount / 10;
            }

            public void OnCommandExecuted(CommandResult commandResult)
            {
                if (commandResult.Status != CommandStatus.Success)
                {
                    _logger.Info("Command execute failed.");
                    return;
                }
                var currentCount = Interlocked.Increment(ref _executedCount);
                if (_isUpdating && currentCount % _printSize == 0)
                {
                    Console.WriteLine("----Processed {0} commands, timespent:{1}ms", currentCount, _watch.ElapsedMilliseconds);
                }
                if (currentCount == _totalCommandCount)
                {
                    _waitHandle.Set();
                }
            }
            public void Add(IAggregateRoot aggregateRoot)
            {
                if (aggregateRoot == null)
                {
                    throw new ArgumentNullException("aggregateRoot");
                }
                if (!_aggregateRoots.TryAdd(aggregateRoot.UniqueId, aggregateRoot))
                {
                    throw new AggregateRootAlreadyExistException(aggregateRoot.UniqueId, aggregateRoot.GetType());
                }
            }
            public T Get<T>(object id, bool firstFormCache = true) where T : class, IAggregateRoot
            {
                if (id == null)
                {
                    throw new ArgumentNullException("id");
                }

                IAggregateRoot aggregateRoot = null;
                if (_aggregateRoots.TryGetValue(id.ToString(), out aggregateRoot))
                {
                    return aggregateRoot as T;
                }

                aggregateRoot = _repository.Get<T>(id);

                if (aggregateRoot != null)
                {
                    _aggregateRoots.TryAdd(aggregateRoot.UniqueId, aggregateRoot);
                    return aggregateRoot as T;
                }

                return null;
            }
            public IEnumerable<IAggregateRoot> GetTrackedAggregateRoots()
            {
                return _aggregateRoots.Values;
            }
            public void Clear()
            {
                _aggregateRoots.Clear();
                _result = null;
            }
            public void SetResult(string result)
            {
                _result = result;
            }
            public string GetResult()
            {
                return _result;
            }
        }
    }
}
