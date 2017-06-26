﻿#region License
// The PostgreSQL License
//
// Copyright (C) 2017 The Npgsql Development Team
//
// Permission to use, copy, modify, and distribute this software and its
// documentation for any purpose, without fee, and without a written
// agreement is hereby granted, provided that the above copyright notice
// and this paragraph and the following two paragraphs appear in all copies.
//
// IN NO EVENT SHALL THE NPGSQL DEVELOPMENT TEAM BE LIABLE TO ANY PARTY
// FOR DIRECT, INDIRECT, SPECIAL, INCIDENTAL, OR CONSEQUENTIAL DAMAGES,
// INCLUDING LOST PROFITS, ARISING OUT OF THE USE OF THIS SOFTWARE AND ITS
// DOCUMENTATION, EVEN IF THE NPGSQL DEVELOPMENT TEAM HAS BEEN ADVISED OF
// THE POSSIBILITY OF SUCH DAMAGE.
//
// THE NPGSQL DEVELOPMENT TEAM SPECIFICALLY DISCLAIMS ANY WARRANTIES,
// INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY
// AND FITNESS FOR A PARTICULAR PURPOSE. THE SOFTWARE PROVIDED HEREUNDER IS
// ON AN "AS IS" BASIS, AND THE NPGSQL DEVELOPMENT TEAM HAS NO OBLIGATIONS
// TO PROVIDE MAINTENANCE, SUPPORT, UPDATES, ENHANCEMENTS, OR MODIFICATIONS.
#endregion

using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Npgsql.Logging;
using Npgsql.PostgresTypes;
using Npgsql.TypeHandlers;
using NpgsqlTypes;

namespace Npgsql.TypeMapping
{
    class ConnectorTypeMapper : TypeMapperBase
    {
        /// <summary>
        /// The connector to which this type mapper belongs.
        /// </summary>
        [CanBeNull]
        readonly NpgsqlConnector _connector;

        /// <summary>
        /// Type information for the database of this mapper. Null for the global mapper.
        /// </summary>
        internal DatabaseInfo DatabaseInfo { get; set; }

        internal TypeHandler UnrecognizedTypeHandler { get; }

        readonly Dictionary<uint, TypeHandler> _byOID = new Dictionary<uint, TypeHandler>();
        readonly Dictionary<NpgsqlDbType, TypeHandler> _byNpgsqlDbType = new Dictionary<NpgsqlDbType, TypeHandler>();
        readonly Dictionary<DbType, TypeHandler> _byDbType = new Dictionary<DbType, TypeHandler>();

        /// <summary>
        /// Maps CLR types to their type handlers.
        /// </summary>
        readonly Dictionary<Type, TypeHandler> _byClrType = new Dictionary<Type, TypeHandler>();

        /// <summary>
        /// Maps CLR types to their array handlers.
        /// </summary>
        readonly Dictionary<Type, TypeHandler> _arrayHandlerByClrType = new Dictionary<Type, TypeHandler>();

        /// <summary>
        /// Copy of <see cref="GlobalTypeMapper.ChangeCounter"/> at the time when this
        /// mapper was created.
        /// </summary>
        internal int ChangeCounter { get; }

        /// <summary>
        /// Whether any mappings have been modified on this concrete type mapper - if so
        /// we must discard it when the pooled connector is reused
        /// </summary>
        internal bool IsModified { get; private set; }

        static readonly NpgsqlLogger Log = NpgsqlLogManager.GetCurrentClassLogger();

        #region Construction

        internal ConnectorTypeMapper(NpgsqlConnector connector)
        {
            _connector = connector;
            UnrecognizedTypeHandler = new UnknownTypeHandler(_connector.Connection);
            ClearBindings();
            ResetMappings();
            ChangeCounter = GlobalTypeMapper.Instance.ChangeCounter;
        }

        #endregion Constructors

        #region Type handler lookup

        /// <summary>
        /// Looks up a type handler by its PostgreSQL type's OID.
        /// </summary>
        /// <param name="oid">A PostgreSQL type OID</param>
        /// <returns>A type handler that can be used to encode and decode values.</returns>
        internal TypeHandler this[uint oid]
        {
            get => TryGetByOID(oid, out var result) ? result : UnrecognizedTypeHandler;
            set => _byOID[oid] = value;
        }

        internal bool TryGetByOID(uint oid, out TypeHandler handler)
        {
            if (_byOID.TryGetValue(oid, out handler))
                return true;
            // TODO: Late activation (composite/enum)
            return false;
        }

        internal TypeHandler this[NpgsqlDbType npgsqlDbType, Type specificType = null]
        {
            get
            {
                if (specificType != null && (npgsqlDbType & NpgsqlDbType.Enum) == 0 && (npgsqlDbType & NpgsqlDbType.Composite) == 0)
                    throw new ArgumentException($"{nameof(specificType)} can only be used with {nameof(NpgsqlDbType.Enum)} or {nameof(NpgsqlDbType.Composite)}");

                if (_byNpgsqlDbType.TryGetValue(npgsqlDbType, out var handler))
                    return handler;

                if (specificType != null)  // Enum/composite
                {
                    if ((npgsqlDbType & NpgsqlDbType.Array) == 0)
                    {
                        if (_byClrType.TryGetValue(specificType, out handler))
                            return handler;
                    }
                    else
                    {
                        // Array of enum/composite
                        if (_arrayHandlerByClrType.TryGetValue(specificType, out handler))
                            return handler;
                    }
                    throw new InvalidOperationException($"The CLR type {specificType.Name} must be mapped with Npgsql before usage, please refer to the documentation.");
                }

                // Couldn't find already activated type, attempt to activate

                if (npgsqlDbType == NpgsqlDbType.Enum || npgsqlDbType == NpgsqlDbType.Composite)
                    throw new InvalidCastException($"When specifying NpgsqlDbType.{nameof(NpgsqlDbType.Enum)}, {nameof(NpgsqlParameter.SpecificType)} must be specified as well");

                throw new NpgsqlException($"The NpgsqlDbType '{npgsqlDbType}' isn't present in your database. " +
                                          "You may need to install an extension or upgrade to a newer version.");
            }
        }

        internal TypeHandler this[DbType dbType]
        {
            get
            {
                if (_byDbType.TryGetValue(dbType, out var handler))
                    return handler;
                throw new NotSupportedException("This DbType is not supported in Npgsql: " + dbType);
            }
        }

        internal TypeHandler this[object value]
        {
            get
            {
                Debug.Assert(value != null);

                if (value is DateTime)
                {
                    return ((DateTime)value).Kind == DateTimeKind.Utc
                        ? this[NpgsqlDbType.TimestampTz]
                        : this[NpgsqlDbType.Timestamp];
                }

                if (value is NpgsqlDateTime)
                {
                    return ((NpgsqlDateTime)value).Kind == DateTimeKind.Utc
                        ? this[NpgsqlDbType.TimestampTz]
                        : this[NpgsqlDbType.Timestamp];
                }

                return this[value.GetType()];
            }
        }

#pragma warning disable CA1043
        internal TypeHandler this[Type type]
#pragma warning restore CA1043
        {
            get
            {
                if (_byClrType.TryGetValue(type, out var handler))
                    return handler;

                // Try to see if it is an array type
                var arrayElementType = GetArrayElementType(type);
                if (arrayElementType != null)
                {
                    if (_arrayHandlerByClrType.TryGetValue(arrayElementType, out var elementHandler))
                        return elementHandler;
                    throw new NotSupportedException($"The CLR array type {type} isn't supported by Npgsql or your PostgreSQL. " +
                                                    "If you wish to map it to an  PostgreSQL composite type array you need to register it before usage, please refer to the documentation.");
                }

                // Nothing worked
                if (type.GetTypeInfo().IsEnum)
                    throw new NotSupportedException($"The CLR enum type {type.Name} must be registered with Npgsql before usage, please refer to the documentation.");

                if (typeof(IEnumerable).IsAssignableFrom(type))
                    throw new NotSupportedException("Npgsql 3.x removed support for writing a parameter with an IEnumerable value, use .ToList()/.ToArray() instead");

                throw new NotSupportedException($"The CLR type {type} isn't supported by Npgsql or your PostgreSQL. " +
                                                "If you wish to map it to a PostgreSQL composite type you need to register it before usage, please refer to the documentation.");
            }
        }

        [CanBeNull]
        static Type GetArrayElementType(Type type)
        {
            var typeInfo = type.GetTypeInfo();
            if (typeInfo.IsArray)
                return type.GetElementType();

            var ilist = typeInfo.ImplementedInterfaces.FirstOrDefault(x => x.GetTypeInfo().IsGenericType && x.GetGenericTypeDefinition() == typeof(IList<>));
            if (ilist != null)
                return ilist.GetGenericArguments()[0];

            if (typeof(IList).IsAssignableFrom(type))
                throw new NotSupportedException("Non-generic IList is a supported parameter, but the NpgsqlDbType parameter must be set on the parameter");

            return null;
        }

        #endregion Type handler lookup

        #region Mapping management

        public override INpgsqlTypeMapper AddMapping(NpgsqlTypeMapping mapping)
        {
            CheckReady();

            base.AddMapping(mapping);
            BindType(mapping, _connector, true);
            IsModified = true;
            return this;
        }

        public override bool RemoveMapping(string pgTypeName)
        {
            CheckReady();

            var removed = base.RemoveMapping(pgTypeName);
            if (!removed)
                return false;

            // Rebind everything. We redo rather than trying to update the
            // existing dictionaries because it's complex to remove arrays, ranges...
            ClearBindings();
            BindTypes();
            IsModified = true;
            return true;
        }

        void CheckReady()
        {
            if (_connector.State != ConnectorState.Ready)
                throw new InvalidOperationException("Connection must be open and idle to perform registration");
        }

        void ResetMappings()
        {
            var globalMapper = GlobalTypeMapper.Instance;
            globalMapper.Lock.EnterReadLock();
            try
            {
                Mappings = new Dictionary<string, NpgsqlTypeMapping>(globalMapper.Mappings);
            }
            finally
            {
                globalMapper.Lock.ExitReadLock();
            }
        }

        void ClearBindings()
        {
            _byOID.Clear();
            _byNpgsqlDbType.Clear();
            _byDbType.Clear();
            _byClrType.Clear();
            _arrayHandlerByClrType.Clear();

            _byNpgsqlDbType[NpgsqlDbType.Unknown] = UnrecognizedTypeHandler;
            _byClrType[typeof(DBNull)] = UnrecognizedTypeHandler;
        }

        public override void Reset()
        {
            ClearBindings();
            ResetMappings();
            BindTypes();
            IsModified = false;
        }

        #endregion Mapping management

        #region Binding

        internal static async Task Bind(NpgsqlConnector connector, NpgsqlTimeout timeout, bool async)
        {
            // Note that there's a chicken and egg problem here - PostgresDatabase.Load below needs a functional
            // connector to load the types, hence the strange initialization code here

            var mapper = new ConnectorTypeMapper(connector);
            connector.TypeMapper = mapper;

            // If we've connected to this specific database before, we have a PostgresDatabase instance
            // (containing all type information)
            var connString = connector.ConnectionString;
            if (!DatabaseInfo.Cache.TryGetValue(connString, out var database))
                DatabaseInfo.Cache[connString] = database = await DatabaseInfo.Load(connector, timeout, async);

            mapper.DatabaseInfo = database;
            mapper.BindTypes();
        }

        void BindTypes()
        {
            lock (DatabaseInfo)  // We lock because LoadComposite mutates the internal state
            {
                foreach (var mapping in Mappings.Values)
                    BindType(mapping, _connector, false);

                // Expose unmapped enums as text
                var textHandlerFactory = new TextHandlerFactory();
                foreach (var e in DatabaseInfo.EnumTypes.Where(e => !_byOID.ContainsKey(e.OID)))
                    BindType(textHandlerFactory.Create(e, _connector.Connection), e);

                // Wire up any domains we find to their base type mappings, this is important
                // for reading domain fields of composites
                foreach (var domain in DatabaseInfo.DomainTypes)
                    if (_byOID.TryGetValue(domain.BaseType.OID, out var baseTypeHandler))
                        _byOID[domain.OID] = baseTypeHandler;
            }
        }

        void BindType(NpgsqlTypeMapping mapping, NpgsqlConnector connector, bool throwOnError)
        {
            // Binding can occur at two different times:
            // 1. When a user adds a mapping for a specific connection (and exception should bubble up to them)
            // 2. When binding the global mappings, in which case we want to log rather than throw
            // (i.e. missing database type for some unused defined binding shouldn't fail the connection)
            try
            {
                DoBindType(mapping, connector);
            }
            catch (Exception e)
            {
                if (throwOnError)
                    throw;
                Log.Warn($"Exception while binding type {mapping.PgTypeName}", e);
                return;
            }
        }

        void DoBindType(NpgsqlTypeMapping mapping, NpgsqlConnector connector)
        {
            var pgName = mapping.PgTypeName;
            var i = pgName.IndexOf('.');
            var found = (i == -1)
                ? DatabaseInfo.ByName.TryGetValue(pgName, out var pgType)  // No dot, partial type name
                : DatabaseInfo.ByFullName.TryGetValue(pgName, out pgType); // Full type name with namespace

            if (!found)
            {
                // We didn't find the type.

                // Somewhat hacky. We don't eagerly load composite types into DatabaseInfo
                // because there could be a great deal of them (#1126). So we check whether
                // the mapping was for a composite and load it now if so
                if (mapping.TypeHandlerFactory is ICompositeTypeHandlerFactory)
                    pgType = DatabaseInfo.GetComposite(mapping.PgTypeName, connector.Connection);
                else
                    throw new ArgumentException($"A PostgreSQL type with the name {mapping.PgTypeName} was not found in the database");
            }

            if (pgType == null)
                throw new ArgumentException($"More than one PostgreSQL type was found with the name {mapping.PgTypeName}, please specify a full name including schema");

            if (pgType is PostgresDomainType)
                throw new NotSupportedException("Cannot add a mapping to a PostgreSQL domain type");

            var handler = mapping.TypeHandlerFactory.Create(pgType, connector.Connection);
            BindType(handler, pgType, mapping.NpgsqlDbType, mapping.DbTypes, mapping.ClrTypes);
        }

        void BindType(TypeHandler handler, PostgresType pgType, NpgsqlDbType? npgsqlDbType = null, DbType[] dbTypes = null, Type[] clrTypes = null)
        {
            _byOID[pgType.OID] = handler;

            if (npgsqlDbType.HasValue)
            {
                var value = npgsqlDbType.Value;
                if (_byNpgsqlDbType.ContainsKey(value))
                    throw new InvalidOperationException($"Two type handlers registered on same NpgsqlDbType '{npgsqlDbType}': {_byNpgsqlDbType[value].GetType().Name} and {handler.GetType().Name}");
                _byNpgsqlDbType[npgsqlDbType.Value] = handler;
            }

            if (dbTypes != null)
            {
                foreach (var dbType in dbTypes)
                {
                    if (_byDbType.ContainsKey(dbType))
                        throw new InvalidOperationException($"Two type handlers registered on same DbType {dbType}: {_byDbType[dbType].GetType().Name} and {handler.GetType().Name}");
                    _byDbType[dbType] = handler;
                }
            }

            if (clrTypes != null)
            {
                foreach (var type in clrTypes)
                {
                    if (_byClrType.ContainsKey(type))
                        throw new InvalidOperationException($"Two type handlers registered on same .NET type '{type}': {_byClrType[type].GetType().Name} and {handler.GetType().Name}");
                    _byClrType[type] = handler;
                }
            }

            if (pgType.Array != null)
                BindArrayType(handler, pgType.Array, npgsqlDbType, clrTypes);

            if (pgType.Range != null)
                BindRangeType(handler, pgType.Range, npgsqlDbType, clrTypes);
        }

        void BindArrayType(TypeHandler elementHandler, PostgresArrayType pgArrayType, NpgsqlDbType? elementNpgsqlDbType, Type[] elementClrTypes)
        {
            var arrayHandler = elementHandler.CreateArrayHandler(pgArrayType);

            var arrayNpgsqlDbType = elementNpgsqlDbType.HasValue
                ? NpgsqlDbType.Array | elementNpgsqlDbType.Value
                : (NpgsqlDbType?)null;

            BindType(arrayHandler, pgArrayType, arrayNpgsqlDbType);

            // Note that array handlers aren't registered in ByClrType like base types, because they handle all
            // dimension types and not just one CLR type (e.g. int[], int[,], int[,,]).
            // So the by-type lookup is special and goes via _arrayHandlerByClrType, see this[Type type]
            // TODO: register single-dimensional in _byType as a specific optimization? But do PSV as well...
            if (elementClrTypes != null)
            {
                foreach (var elementType in elementClrTypes)
                {
                    if (_arrayHandlerByClrType.ContainsKey(elementType))
                        throw new Exception(
                            $"Two array type handlers registered on same .NET type {elementType}: {_arrayHandlerByClrType[elementType].GetType().Name} and {arrayHandler.GetType().Name}");
                    _arrayHandlerByClrType[elementType] = arrayHandler;
                }
            }
        }

        void BindRangeType(TypeHandler elementHandler, PostgresRangeType pgRangeType, NpgsqlDbType? elementNpgsqlDbType, Type[] elementClrTypes)
        {
            var rangeHandler = elementHandler.CreateRangeHandler(pgRangeType);

            var rangeNpgsqlDbType = elementNpgsqlDbType.HasValue
                ? NpgsqlDbType.Range | elementNpgsqlDbType.Value
                : (NpgsqlDbType?)null;


            Type[] clrTypes = null;
            if (elementClrTypes != null)
            {
                // Somewhat hacky. Although the element may have more than one CLR mapping,
                // its range will only be mapped to the "main" one for now.
                var defaultElementType = elementHandler.GetFieldType();

                clrTypes = elementClrTypes.Contains(defaultElementType)
                    ? new[] { rangeHandler.GetFieldType() }
                    : null;
            }

            BindType(rangeHandler, pgRangeType, rangeNpgsqlDbType, null, clrTypes);
        }

#if MAPPING_FIRST
        internal void BindTypes(NpgsqlConnector connector)
        {
            Debug.Assert(Database != null, "Attempting to activate the global type mapper");

            // Array and range types get bound through their element types

            foreach (var pgType in Database.BaseTypes)
            {
                if (!TryGetMapping(pgType, out var mapping))
                    continue;  // Unmapped PostgreSQL type

                var handler = mapping.TypeHandlerFactory.Create(pgType, connector.Connection);
                BindType(handler, pgType, mapping.NpgsqlDbType, mapping.DbTypes, mapping.ClrTypes);
            }

            foreach (var pgType in Database.EnumTypes)
            {
                if (!TryGetMapping(pgType, out var mapping))
                {
                    // Unmapped enum, bind to TextHandler to allow retrieving values as text
                    var textHandler = new TextHandler(pgType, connector.TextEncoding);
                    BindType(textHandler, pgType);
                    continue;
                }

                // Mapped enum
                var handler = mapping.TypeHandlerFactory.Create(pgType, connector.Connection);
                BindType(handler, pgType, null, null, mapping.ClrTypes);
            }
        }

        bool TryGetMapping(PostgresType pgType, out TypeMapping mapping)
        {
            // Mapping with full name
            if (_mappings.TryGetValue(pgType.FullName, out mapping))
                return true;
            if (!_mappings.TryGetValue(pgType.Name, out mapping))
                return false;
            // Found mapping with partial name, but make sure there's only one PG type that matches
            if (Database.ByName[pgType.Name] == null)
            {
                Log.Error($"A mapping exists for PostgreSQL type {pgType.Name}, but multiple types match in different schemas." +
                            "Skipping the mapping, use a fully-qualified name.");
                return false;
            }
            Debug.Assert(Database.ByName[pgType.Name] == pgType);
            return true;
        }
#endif

        #endregion Binding
    }
}
