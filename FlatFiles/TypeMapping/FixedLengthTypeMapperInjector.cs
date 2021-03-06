﻿using System;
using System.Collections.Generic;
using System.IO;
using FlatFiles.Properties;

namespace FlatFiles.TypeMapping
{
    /// <summary>
    /// Allows specifying which schema to use when a predicate is matched.
    /// </summary>
    public interface IFixedLengthTypeMapperInjectorWhenBuilder
    {
        /// <summary>
        /// Specifies which type mapper to use when the predicate is matched.
        /// </summary>
        /// <param name="mapper">The type mapper to use.</param>
        /// <returns>The type mapper selector.</returns>
        void Use(IDynamicFixedLengthTypeMapper mapper);
    }

    /// <summary>
    /// Allows specifying which schema to use when a predicate is matched.
    /// </summary>
    /// <typeparam name="TEntity">The type of the entity mapped by the mapper.</typeparam>
    public interface IFixedLengthTypeMapperInjectorWhenBuilder<TEntity>
    {
        /// <summary>
        /// Specifies which type mapper to use when the predicate is matched.
        /// </summary>
        /// <param name="mapper">The type mapper to use.</param>
        /// <returns>The type mapper selector.</returns>
        void Use(IFixedLengthTypeMapper<TEntity> mapper);
    }

    /// <summary>
    /// Represents a class that can dynamically map types based on the shape of the record.
    /// </summary>
    public class FixedLengthTypeMapperInjector : ITypeMapperInjector
    {
        private static readonly TypeMapperMatcher nonMatcher = new TypeMapperMatcher { Predicate = o => false };
        private readonly List<TypeMapperMatcher> matchers = new List<TypeMapperMatcher>();
        private TypeMapperMatcher defaultMatcher = nonMatcher;

        /// <summary>
        /// Indicates that the given schema should be used when the predicate returns true.
        /// </summary>
        /// <param name="predicate">Indicates whether the schema should be used for a record.</param>
        /// <returns>An object for specifying which schema to use when the predicate matches.</returns>
        /// <remarks>Previously registered schemas will be used if their predicates match.</remarks>
        public IFixedLengthTypeMapperInjectorWhenBuilder<TEntity> When<TEntity>(Func<TEntity, bool> predicate = null)
        {
            return new FixedLengthTypeMapperInjectorWhenBuilder<TEntity>(this, predicate);
        }

        /// <summary>
        /// Indicates that the given schema should be used when the predicate returns true.
        /// </summary>
        /// <param name="predicate">Indicates whether the schema should be used for a record.</param>
        /// <returns>An object for specifying which schema to use when the predicate matches.</returns>
        /// <exception cref="System.ArgumentException">The predicate is null.</exception>
        /// <remarks>Previously registered schemas will be used if their predicates match.</remarks>
        public IFixedLengthTypeMapperInjectorWhenBuilder When(Func<object, bool> predicate)
        {
            if (predicate == null)
            {
                throw new ArgumentNullException(nameof(predicate));
            }
            return new FixedLengthTypeMapperInjectorWhenBuilder(this, predicate);
        }

        /// <summary>
        /// Provides the schema to use by default when no other matches are found.
        /// </summary>
        /// <param name="typeMapper">The default type mapper to use.</param>
        public void WithDefault<TEntity>(IFixedLengthTypeMapper<TEntity> typeMapper)
        {
            WithDefault((IDynamicFixedLengthTypeMapper)typeMapper);
        }

        /// <summary>
        /// Provides the schema to use by default when no other matches are found.
        /// </summary>
        /// <param name="typeMapper">The default schema to use.</param>
        public void WithDefault(IDynamicFixedLengthTypeMapper typeMapper)
        {
            defaultMatcher = typeMapper == null ? nonMatcher : new TypeMapperMatcher
            {
                TypeMapper = typeMapper,
                Predicate = o => true
            };
        }

        /// <summary>
        /// Gets a typed writer for writing the objects to the file.
        /// </summary>
        /// <param name="writer">The writer to use.</param>
        /// <param name="options">The separate value options to use.</param>
        /// <returns>The typed writer.</returns>
        public ITypedWriter<object> GetWriter(TextWriter writer, FixedLengthOptions options = null)
        {
            var injector = new FixedLengthSchemaInjector();
            var valueWriter = new FixedLengthWriter(writer, injector, options);
            var multiWriter = new MultiplexingTypedWriter(valueWriter, this);
            foreach (var matcher in matchers)
            {
                matcher.Reset();
                injector.When(values => matcher.IsMatch).Use(matcher.Schema);
            }
            if (defaultMatcher != nonMatcher)
            {
                defaultMatcher.Reset();
                injector.WithDefault(defaultMatcher.Schema);
            }
            return multiWriter;
        }

        internal void Add(IDynamicFixedLengthTypeMapper typeMapper, Func<object, bool> predicate)
        {
            matchers.Add(new TypeMapperMatcher()
            {
                TypeMapper = typeMapper,
                Predicate = predicate
            });
        }

        (ISchema, int, Action<IRecordContext, object, object[]>) ITypeMapperInjector.SetMatcher(object entity)
        {
            ISchema schema = null;
            int logicalCount = 0;
            Action<IRecordContext, object, object[]> serializer = null;
            foreach (var matcher in matchers)
            {
                if (serializer == null && matcher.Predicate(entity))
                {
                    matcher.IsMatch = true;
                    matcher.Initialize();
                    schema = matcher.Schema;
                    logicalCount = matcher.WorkCount;
                    serializer = matcher.Serializer;
                }
                else
                {
                    matcher.IsMatch = false;
                }
            }
            if (serializer == null)
            {
                if (defaultMatcher == nonMatcher)
                {
                    throw new FlatFileException(Resources.MissingMatcher);
                }
                defaultMatcher.Initialize();
                schema = defaultMatcher.Schema;
                logicalCount = defaultMatcher.WorkCount;
                serializer = defaultMatcher.Serializer;
            }
            return (schema, logicalCount, serializer);
        }

        private class TypeMapperMatcher
        {
            public IDynamicFixedLengthTypeMapper TypeMapper { get; set; }

            public Func<object, bool> Predicate { get; set; }

            public bool IsMatch { get; set; }

            public FixedLengthSchema Schema { get; set; }

            public int WorkCount { get; set; }

            public Action<IRecordContext, object, object[]> Serializer { get; set; }

            public void Reset()
            {
                Schema = TypeMapper.GetSchema();
                WorkCount = 0;
                Serializer = null;
            }

            public void Initialize()
            {
                if (Serializer != null)
                {
                    return;
                }
                var source = (IMapperSource)TypeMapper;
                var mapper = source.GetMapper();
                WorkCount = mapper.LogicalCount;
                Serializer = mapper.GetWriter();
            }
        }

        private class FixedLengthTypeMapperInjectorWhenBuilder : IFixedLengthTypeMapperInjectorWhenBuilder
        {
            private readonly FixedLengthTypeMapperInjector selector;
            private readonly Func<object, bool> predicate;

            public FixedLengthTypeMapperInjectorWhenBuilder(FixedLengthTypeMapperInjector selector, Func<object, bool> predicate)
            {
                this.selector = selector;
                this.predicate = predicate;
            }

            public void Use(IDynamicFixedLengthTypeMapper typeMapper)
            {
                if (typeMapper == null)
                {
                    throw new ArgumentNullException(nameof(typeMapper));
                }
                selector.Add(typeMapper, predicate);
            }
        }

        private class FixedLengthTypeMapperInjectorWhenBuilder<TEntity> : IFixedLengthTypeMapperInjectorWhenBuilder<TEntity>
        {
            private static readonly Func<object, bool> typeCheck = o => o is TEntity;
            private readonly FixedLengthTypeMapperInjector selector;
            private readonly Func<object, bool> predicate;

            public FixedLengthTypeMapperInjectorWhenBuilder(FixedLengthTypeMapperInjector selector, Func<TEntity, bool> predicate)
            {
                this.selector = selector;
                this.predicate = predicate == null ? typeCheck : o => o is TEntity entity && predicate(entity);
            }

            public void Use(IFixedLengthTypeMapper<TEntity> typeMapper)
            {
                if (typeMapper == null)
                {
                    throw new ArgumentNullException(nameof(typeMapper));
                }
                var dynamicMapper = (IDynamicFixedLengthTypeMapper)typeMapper;
                selector.Add(dynamicMapper, predicate);
            }
        }
    }
}
