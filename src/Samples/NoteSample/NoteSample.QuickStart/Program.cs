﻿using System;
using System.Reflection;
using ECommon.Components;
using ECommon.Configurations;
using ECommon.Logging;
using ECommon.Utilities;
using ENode.Commanding;
using ENode.Configurations;
using NoteSample.Commands;

namespace NoteSample.QuickStart
{
    class Program
    {
        static ILogger _logger;
        static ENodeConfiguration _configuration;

        static void Main(string[] args)
        {
            InitializeENodeFramework();

            var commandService = ObjectContainer.Resolve<ICommandService>();
            var noteId = ObjectId.GenerateNewStringId();
            var command1 = new CreateNoteCommand { AggregateRootId = noteId, Title = "Sample Title1" };
            var command2 = new ChangeNoteTitleCommand { AggregateRootId = noteId, Title = "Sample Title2" };

            Console.WriteLine(string.Empty);

            commandService.ExecuteAsync(command1, CommandReturnType.EventHandled).Wait();
            commandService.ExecuteAsync(command2, CommandReturnType.EventHandled).Wait();

            Console.WriteLine(string.Empty);

            _logger.Info("Press Enter to exit...");

            Console.ReadLine();
            _configuration.ShutdownEQueue();
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
            _configuration = Configuration
                .Create()
                .UseAutofac()
                .RegisterCommonComponents()
                .UseLog4Net()
                .UseJsonNet()
                .RegisterUnhandledExceptionHandler()
                .CreateENode()
                .RegisterENodeComponents()
                .RegisterBusinessComponents(assemblies)
                .UseEQueue()
                .InitializeBusinessAssemblies(assemblies)
                .StartEQueue();

            Console.WriteLine(string.Empty);

            _logger = ObjectContainer.Resolve<ILoggerFactory>().Create(typeof(Program).Name);
            _logger.Info("ENode started...");
        }
    }
}
