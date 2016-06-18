﻿using System;
using System.Collections.Generic;

namespace ENode.Domain
{
    /// <summary>Represents a high speed memory cache to get or set aggregate.
    /// </summary>
    public interface IMemoryCache
    {
        /// <summary>Get an aggregate from memory cache.
        /// </summary>
        /// <param name="aggregateRootId"></param>
        /// <param name="aggregateRootType"></param>
        /// <returns></returns>
        IAggregateRoot Get(object aggregateRootId, Type aggregateRootType);
        /// <summary>Get a strong type aggregate from memory cache.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="aggregateRootId"></param>
        /// <returns></returns>
        T Get<T>(object aggregateRootId) where T : class, IAggregateRoot;
        /// <summary>Set an aggregate to memory cache.
        /// </summary>
        /// <param name="aggregateRoot"></param>
        void Set(IAggregateRoot aggregateRoot);
        /// <summary>Refresh the aggregate memory cache by replaying events of event store.
        /// </summary>
        void RefreshAggregateFromEventStore(string aggregateRootTypeName, string aggregateRootId);
        /// <summary>Remove an aggregate from memory.
        /// </summary>
        /// <param name="aggregateRootId"></param>
        bool Remove(object aggregateRootId);
        /// <summary>Get all the aggregates from memory cache.
        /// </summary>
        /// <returns></returns>
        IEnumerable<AggregateCacheInfo> GetAll();
    }
}
