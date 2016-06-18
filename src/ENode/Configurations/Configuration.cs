﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ECommon.Components;
using ECommon.Configurations;
using ECommon.Utilities;
using ENode.Commanding;
using ENode.Commanding.Impl;
using ENode.Domain;
using ENode.Domain.Impl;
using ENode.EQueue;
using ENode.Eventing;
using ENode.Eventing.Impl;
using ENode.Infrastructure;
using ENode.Infrastructure.Impl;
using ENode.Infrastructure.Impl.InMemory;
using ENode.Infrastructure.Impl.SQL;

namespace ENode.Configurations
{
    /// <summary>Configuration class for enode framework.
    /// </summary>
    public class ENodeConfiguration
    {
        #region Private Vairables

        private readonly Configuration _configuration;
        private readonly IList<Type> _assemblyInitializerServiceTypes;

        #endregion

        /// <summary>Get the current setting information.
        /// </summary>
        public ConfigurationSetting Setting { get; private set; }

        /// <summary>Provides the singleton access instance.
        /// </summary>
        public static ENodeConfiguration Instance { get; private set; }

        /// <summary>Parameterized constructor.
        /// </summary>
        private ENodeConfiguration(Configuration configuration, ConfigurationSetting setting)
        {
            Ensure.NotNull(configuration, "configuration");
            Ensure.NotNull(setting, "setting");
            _assemblyInitializerServiceTypes = new List<Type>();
            _configuration = configuration;
            Setting = setting;
        }

        /// <summary>Create the enode configuration instance.
        /// </summary>
        /// <param name="configuration"></param>
        /// <returns></returns>
        public static ENodeConfiguration CreateENode(Configuration configuration, ConfigurationSetting setting)
        {
            Instance = new ENodeConfiguration(configuration, setting);
            return Instance;
        }

        /// <summary>Get the ecommon configuration.
        /// </summary>
        /// <returns></returns>
        public Configuration GetCommonConfiguration()
        {
            return _configuration;
        }
        /// <summary>Register all the default components of enode framework.
        /// </summary>
        public ENodeConfiguration RegisterENodeComponents()
        {
            _configuration.SetDefault<ITypeNameProvider, DefaultTypeNameProvider>();
            _configuration.SetDefault<IMessageHandlerProvider, DefaultMessageHandlerProvider>();
            _configuration.SetDefault<ITwoMessageHandlerProvider, DefaultTwoMessageHandlerProvider>();
            _configuration.SetDefault<IThreeMessageHandlerProvider, DefaultThreeMessageHandlerProvider>();

            _configuration.SetDefault<IAggregateRootInternalHandlerProvider, DefaultAggregateRootInternalHandlerProvider>();
            _configuration.SetDefault<IAggregateRepositoryProvider, DefaultAggregateRepositoryProvider>();
            _configuration.SetDefault<IAggregateRootFactory, DefaultAggregateRootFactory>();
            _configuration.SetDefault<IMemoryCache, DefaultMemoryCache>();
            _configuration.SetDefault<ICleanAggregateService, DefaultCleanAggregateService>();
            _configuration.SetDefault<IAggregateSnapshotter, DefaultAggregateSnapshotter>();
            _configuration.SetDefault<IAggregateStorage, EventSourcingAggregateStorage>();
            _configuration.SetDefault<IRepository, DefaultRepository>();

            _configuration.SetDefault<ICommandAsyncHandlerProvider, DefaultCommandAsyncHandlerProvider>();
            _configuration.SetDefault<ICommandHandlerProvider, DefaultCommandHandlerProvider>();
            _configuration.SetDefault<ICommandStore, InMemoryCommandStore>();
            _configuration.SetDefault<ICommandRoutingKeyProvider, DefaultCommandRoutingKeyProvider>();
            _configuration.SetDefault<ICommandService, NotImplementedCommandService>();

            _configuration.SetDefault<IEventSerializer, DefaultEventSerializer>();
            _configuration.SetDefault<IEventStore, InMemoryEventStore>();
            _configuration.SetDefault<IPublishedVersionStore, InMemoryPublishedVersionStore>();
            _configuration.SetDefault<IEventService, DefaultEventService>();

            _configuration.SetDefault<IMessageDispatcher, DefaultMessageDispatcher>();

            _configuration.SetDefault<IMessagePublisher<IApplicationMessage>, DoNothingPublisher>();
            _configuration.SetDefault<IMessagePublisher<DomainEventStreamMessage>, DoNothingPublisher>();
            _configuration.SetDefault<IMessagePublisher<IPublishableException>, DoNothingPublisher>();

            _configuration.SetDefault<IProcessingCommandHandler, DefaultProcessingCommandHandler>();
            _configuration.SetDefault<IProcessingMessageHandler<ProcessingApplicationMessage, IApplicationMessage, bool>, DefaultProcessingMessageHandler<ProcessingApplicationMessage, IApplicationMessage, bool>>();
            _configuration.SetDefault<IProcessingMessageHandler<ProcessingDomainEventStreamMessage, DomainEventStreamMessage, bool>, DomainEventStreamMessageHandler>();
            _configuration.SetDefault<IProcessingMessageHandler<ProcessingPublishableExceptionMessage, IPublishableException, bool>, DefaultProcessingMessageHandler<ProcessingPublishableExceptionMessage, IPublishableException, bool>>();

            _configuration.SetDefault<IProcessingCommandScheduler, DefaultProcessingCommandScheduler>();
            _configuration.SetDefault<IProcessingMessageScheduler<ProcessingApplicationMessage, IApplicationMessage, bool>, DefaultProcessingMessageScheduler<ProcessingApplicationMessage, IApplicationMessage, bool>>();
            _configuration.SetDefault<IProcessingMessageScheduler<ProcessingDomainEventStreamMessage, DomainEventStreamMessage, bool>, DefaultProcessingMessageScheduler<ProcessingDomainEventStreamMessage, DomainEventStreamMessage, bool>>();
            _configuration.SetDefault<IProcessingMessageScheduler<ProcessingPublishableExceptionMessage, IPublishableException, bool>, DefaultProcessingMessageScheduler<ProcessingPublishableExceptionMessage, IPublishableException, bool>>();

            _configuration.SetDefault<ICommandProcessor, DefaultCommandProcessor>();
            _configuration.SetDefault<IMessageProcessor<ProcessingApplicationMessage, IApplicationMessage, bool>, DefaultMessageProcessor<ProcessingApplicationMessage, IApplicationMessage, bool>>();
            _configuration.SetDefault<IMessageProcessor<ProcessingDomainEventStreamMessage, DomainEventStreamMessage, bool>, DefaultMessageProcessor<ProcessingDomainEventStreamMessage, DomainEventStreamMessage, bool>>();
            _configuration.SetDefault<IMessageProcessor<ProcessingPublishableExceptionMessage, IPublishableException, bool>, DefaultMessageProcessor<ProcessingPublishableExceptionMessage, IPublishableException, bool>>();

            _assemblyInitializerServiceTypes.Add(typeof(ITypeNameProvider));
            _assemblyInitializerServiceTypes.Add(typeof(IAggregateRootInternalHandlerProvider));
            _assemblyInitializerServiceTypes.Add(typeof(IAggregateRepositoryProvider));
            _assemblyInitializerServiceTypes.Add(typeof(IMessageHandlerProvider));
            _assemblyInitializerServiceTypes.Add(typeof(ITwoMessageHandlerProvider));
            _assemblyInitializerServiceTypes.Add(typeof(IThreeMessageHandlerProvider));
            _assemblyInitializerServiceTypes.Add(typeof(ICommandHandlerProvider));
            _assemblyInitializerServiceTypes.Add(typeof(ICommandAsyncHandlerProvider));

            return this;
        }
        /// <summary>Register all the business components from the given assemblies.
        /// </summary>
        public ENodeConfiguration RegisterBusinessComponents(params Assembly[] assemblies)
        {
            var registeredTypes = new List<Type>();
            foreach (var assembly in assemblies)
            {
                foreach (var type in assembly.GetTypes().Where(x => IsENodeComponentType(x)))
                {
                    RegisterComponentType(type);
                    registeredTypes.Add(type);
                }
                foreach (var type in assembly.GetTypes().Where(Infrastructure.TypeUtils.IsComponent))
                {
                    if (!registeredTypes.Contains(type))
                    {
                        RegisterComponentType(type);
                    }
                }
            }
            return this;
        }
        /// <summary>Use the SnapshotOnlyAggregateStorage as the IAggregateStorage.
        /// </summary>
        /// <returns></returns>
        public ENodeConfiguration UseSnapshotOnlyAggregateStorage()
        {
            _configuration.SetDefault<IAggregateStorage, SnapshotOnlyAggregateStorage>();
            return this;
        }
        /// <summary>Use the SqlServerLockService as the ILockService.
        /// </summary>
        /// <returns></returns>
        public ENodeConfiguration UseSqlServerLockService(OptionSetting optionSetting = null)
        {
            _configuration.SetDefault<ILockService, SqlServerLockService>(new SqlServerLockService(optionSetting));
            return this;
        }
        /// <summary>Use the SqlServerCommandStore as the ICommandStore.
        /// </summary>
        /// <returns></returns>
        public ENodeConfiguration UseSqlServerCommandStore(OptionSetting optionSetting = null)
        {
            _configuration.SetDefault<ICommandStore, SqlServerCommandStore>(new SqlServerCommandStore(optionSetting));
            return this;
        }
        /// <summary>Use the SqlServerEventStore as the IEventStore.
        /// </summary>
        /// <returns></returns>
        public ENodeConfiguration UseSqlServerEventStore(OptionSetting optionSetting = null)
        {
            _configuration.SetDefault<IEventStore, SqlServerEventStore>(new SqlServerEventStore(optionSetting));
            return this;
        }
        /// <summary>Use the SqlServerPublishedVersionStore as the IPublishedVersionStore.
        /// </summary>
        /// <returns></returns>
        public ENodeConfiguration UseSqlServerPublishedVersionStore(OptionSetting optionSetting = null)
        {
            _configuration.SetDefault<IPublishedVersionStore, SqlServerPublishedVersionStore>(new SqlServerPublishedVersionStore(optionSetting));
            return this;
        }
        /// <summary>Initialize all the assembly initializers with the given business assemblies.
        /// </summary>
        /// <returns></returns>
        public ENodeConfiguration InitializeBusinessAssemblies(params Assembly[] assemblies)
        {
            ValidateTypes(assemblies);
            foreach (var assemblyInitializer in _assemblyInitializerServiceTypes.Select(ObjectContainer.Resolve).OfType<IAssemblyInitializer>())
            {
                assemblyInitializer.Initialize(assemblies);
            }
            return this;
        }

        #region Private Methods

        private void RegisterComponentType(Type type)
        {
            var life = ParseComponentLife(type);
            ObjectContainer.RegisterType(type, null, life);
            foreach (var interfaceType in type.GetInterfaces())
            {
                ObjectContainer.RegisterType(interfaceType, type, null, life);
            }
            if (IsAssemblyInitializer(type))
            {
                _assemblyInitializerServiceTypes.Add(type);
            }
        }
        private bool IsENodeComponentType(Type type)
        {
            return type.IsClass && !type.IsAbstract && type.GetInterfaces().Any(x => x.IsGenericType &&
            (x.GetGenericTypeDefinition() == typeof(ICommandHandler<>)
            || x.GetGenericTypeDefinition() == typeof(ICommandAsyncHandler<>)
            || x.GetGenericTypeDefinition() == typeof(IMessageHandler<>)
            || x.GetGenericTypeDefinition() == typeof(IMessageHandler<,>)
            || x.GetGenericTypeDefinition() == typeof(IMessageHandler<,,>)
            || x.GetGenericTypeDefinition() == typeof(IAggregateRepository<>)
            || x.GetGenericTypeDefinition() == typeof(ITopicProvider<>)));
        }
        private void ValidateTypes(params Assembly[] assemblies)
        {
            foreach (var assembly in assemblies)
            {
                foreach (var type in assembly.GetTypes().Where(x => x.IsClass && (
                    typeof(ICommand).IsAssignableFrom(x) ||
                    typeof(IDomainEvent).IsAssignableFrom(x) ||
                    typeof(IApplicationMessage).IsAssignableFrom(x))))
                {
                    if (!type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Any(x => x.GetParameters().Count() == 0))
                    {
                        throw new Exception(string.Format("{0} must have a default constructor.", type.FullName));
                    }
                }
            }
        }
        private static LifeStyle ParseComponentLife(Type type)
        {
            var attributes = type.GetCustomAttributes<ComponentAttribute>(false);
            if (attributes != null && attributes.Any())
            {
                return attributes.First().LifeStyle;
            }
            return LifeStyle.Singleton;
        }
        private static bool IsAssemblyInitializer<T>()
        {
            return IsAssemblyInitializer(typeof(T));
        }
        private static bool IsAssemblyInitializer(Type type)
        {
            return type.IsClass && !type.IsAbstract && typeof(IAssemblyInitializer).IsAssignableFrom(type);
        }

        #endregion
    }

    public static class ConfigurationExtensions
    {
        public static ENodeConfiguration CreateENode(this Configuration configuration, ConfigurationSetting setting = null)
        {
            return ENodeConfiguration.CreateENode(configuration, setting ?? new ConfigurationSetting());
        }
    }
}