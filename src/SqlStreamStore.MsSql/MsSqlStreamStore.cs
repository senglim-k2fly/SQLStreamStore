﻿namespace SqlStreamStore
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.SqlClient;
    using Microsoft.Data.SqlClient.Server;
    using SqlStreamStore.Imports.Ensure.That;
    using SqlStreamStore.Infrastructure;
    using SqlStreamStore.ScriptsV2;
    using SqlStreamStore.Streams;
    using SqlStreamStore.Subscriptions;

    /// <summary>
    ///     Represents a Microsoft SQL Server stream store implementation.
    /// </summary>
    [Obsolete("Use MsSqlStreamStoreV3 instead. Note: this will require a schema and data migration.", false)]
    public sealed partial class MsSqlStreamStore : StreamStoreBase
    {
        private readonly Func<SqlConnection> _createConnection;
        private readonly Lazy<IStreamStoreNotifier> _streamStoreNotifier;
        private readonly Scripts _scripts;
        private readonly SqlMetaData[] _appendToStreamSqlMetadata;
        private readonly int _commandTimeout;
        public const int FirstSchemaVersion = 1;
        public const int CurrentSchemaVersion = 2;

        /// <summary>
        ///     Initializes a new instance of <see cref="MsSqlStreamStore"/>
        /// </summary>
        /// <param name="settings">A settings class to configure this instance.</param>
        public MsSqlStreamStore(MsSqlStreamStoreSettings settings)
            :base(settings.MetadataMaxAgeCacheExpire, settings.MetadataMaxAgeCacheMaxSize,
                 settings.GetUtcNow, settings.LogName)
        {
            Ensure.That(settings, nameof(settings)).IsNotNull();

            _createConnection = () => settings.ConnectionFactory(settings.ConnectionString);
            _streamStoreNotifier = new Lazy<IStreamStoreNotifier>(() =>
                {
                    if(settings.CreateStreamStoreNotifier == null)
                    {
                        throw new InvalidOperationException(
                            "Cannot create notifier because supplied createStreamStoreNotifier was null");
                    }
                    return settings.CreateStreamStoreNotifier.Invoke(this);
                });
            _scripts = new Scripts(settings.Schema);

            var sqlMetaData = new List<SqlMetaData>
            {
                new SqlMetaData("StreamVersion", SqlDbType.Int, true, false, SortOrder.Unspecified, -1),
                new SqlMetaData("Id", SqlDbType.UniqueIdentifier),
                new SqlMetaData("Created", SqlDbType.DateTime, true, false, SortOrder.Unspecified, -1),
                new SqlMetaData("Type", SqlDbType.NVarChar, 1024),
                new SqlMetaData("JsonData", SqlDbType.NVarChar, SqlMetaData.Max),
                new SqlMetaData("JsonMetadata", SqlDbType.NVarChar, SqlMetaData.Max)
            };

            if(settings.GetUtcNow != null)
            {
                // Created column value will be client supplied so should prevent using of the column default function
                sqlMetaData[2] = new SqlMetaData("Created", SqlDbType.DateTime);
            }

            _appendToStreamSqlMetadata = sqlMetaData.ToArray();
            _commandTimeout = settings.CommandTimeout;
        }

        /// <summary>
        ///     Creates a scheme to hold stream
        /// </summary>
        /// <param name="cancellationToken">The cancellation instruction.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public async Task CreateSchema(CancellationToken cancellationToken = default)
        {
            GuardAgainstDisposed();

            using(var connection = _createConnection())
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                if(_scripts.Schema != "dbo")
                {
                    using(var command = new SqlCommand($@"
                        IF NOT EXISTS (
                        SELECT  schema_name
                        FROM    information_schema.schemata
                        WHERE   schema_name = '{_scripts.Schema}' ) 

                        BEGIN
                        EXEC sp_executesql N'CREATE SCHEMA {_scripts.Schema}'
                        END", connection))
                    {
                        command.CommandTimeout = _commandTimeout;

                        await command
                            .ExecuteNonQueryAsync(cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                using (var command = new SqlCommand(_scripts.CreateSchema, connection))
                {
                    command.CommandTimeout = _commandTimeout;

                    await command.ExecuteNonQueryAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        internal async Task CreateSchema_v1_ForTests(CancellationToken cancellationToken = default)
        {
            GuardAgainstDisposed();

            using (var connection = _createConnection())
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                if (_scripts.Schema != "dbo")
                {
                    using (var command = new SqlCommand($@"
                        IF NOT EXISTS (
                        SELECT  schema_name
                        FROM    information_schema.schemata
                        WHERE   schema_name = '{_scripts.Schema}' ) 

                        BEGIN
                        EXEC sp_executesql N'CREATE SCHEMA {_scripts.Schema}'
                        END", connection))
                    {
                        command.CommandTimeout = _commandTimeout;

                        await command
                            .ExecuteNonQueryAsync(cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                using (var command = new SqlCommand(_scripts.CreateSchema_v1, connection))
                {
                    command.CommandTimeout = _commandTimeout;

                    await command.ExecuteNonQueryAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns>A <see cref="CheckSchemaResult"/> representing the result of the operation.</returns>
        public async Task<CheckSchemaResult> CheckSchema(
            CancellationToken cancellationToken = default)
        {
            GuardAgainstDisposed();

            using(var connection = _createConnection())
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                using (var command = new SqlCommand(_scripts.GetSchemaVersion, connection))
                {
                    command.CommandTimeout = _commandTimeout;

                    var extendedProperties =  await command
                        .ExecuteReaderAsync(cancellationToken)
                        .ConfigureAwait(false);

                    int? version = null;
                    while(await extendedProperties.ReadAsync(cancellationToken))
                    {
                        if(extendedProperties.GetString(0) != "version")
                            continue;
                        version = int.Parse(extendedProperties.GetString(1));
                        break;
                    }

                    return version == null
                        ? new CheckSchemaResult(FirstSchemaVersion, CurrentSchemaVersion)  // First schema (1) didn't have extended properties.
                        : new CheckSchemaResult(int.Parse(version.ToString()), CurrentSchemaVersion);
                }
            }
        }

        /// <summary>
        ///     Drops all tables related to this store instance.
        /// </summary>
        /// <param name="cancellationToken">The cancellation instruction.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public async Task DropAll(CancellationToken cancellationToken = default)
        {
            GuardAgainstDisposed();

            using(var connection = _createConnection())
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                using(var command = new SqlCommand(_scripts.DropAll, connection))
                {
                    command.CommandTimeout = _commandTimeout;

                    await command
                        .ExecuteNonQueryAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        private async Task<int> GetStreamMessageCount(
            string streamId,
            CancellationToken cancellationToken = default)
        {
            GuardAgainstDisposed();

            using(var connection = _createConnection())
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                using(var command = new SqlCommand(_scripts.GetStreamMessageCount, connection))
                {
                    var streamIdInfo = new StreamIdInfo(streamId);
                    command.CommandTimeout = _commandTimeout;
                    command.Parameters.Add(new SqlParameter("streamId", SqlDbType.Char, 42) { Value = streamIdInfo.SqlStreamId.Id });

                    var result = await command
                        .ExecuteScalarAsync(cancellationToken)
                        .ConfigureAwait(false);

                    return (int) result;
                }
            }
        }

        public async Task<int> GetmessageCount(
            string streamId,
            DateTime createdBefore,
            CancellationToken cancellationToken = default)
        {
            GuardAgainstDisposed();

            using (var connection = _createConnection())
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                using (var command = new SqlCommand(_scripts.GetStreamMessageBeforeCreatedCount, connection))
                {
                    var streamIdInfo = new StreamIdInfo(streamId);
                    command.CommandTimeout = _commandTimeout;
                    command.Parameters.Add(new SqlParameter("streamId", SqlDbType.Char, 42) { Value = streamIdInfo.SqlStreamId.Id });
                    command.Parameters.AddWithValue("created", createdBefore);

                    var result = await command
                        .ExecuteScalarAsync(cancellationToken)
                        .ConfigureAwait(false);

                    return (int)result;
                }
            }
        }

        protected override async Task<long> ReadHeadPositionInternal(CancellationToken cancellationToken)
        {
            GuardAgainstDisposed();

            using(var connection = _createConnection())
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                using(var command = new SqlCommand(_scripts.ReadHeadPosition, connection))
                {
                    command.CommandTimeout = _commandTimeout;
                    var result = await command
                        .ExecuteScalarAsync(cancellationToken)
                        .ConfigureAwait(false);

                    if(result == DBNull.Value)
                    {
                        return Position.End;
                    }
                    return (long) result;
                }
            }
        }

        protected override async Task<long> ReadStreamHeadPositionInternal(string streamId, CancellationToken cancellationToken)
        {
            GuardAgainstDisposed();

            using(var connection = _createConnection())
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                using(var command = new SqlCommand(_scripts.ReadStreamHeadPosition, connection))
                {
                    command.CommandTimeout = _commandTimeout;
                    command.Parameters.Add(new SqlParameter("streamId", SqlDbType.Char, 42) { Value = new StreamIdInfo(streamId).SqlStreamId.Id });
                    var result = await command
                        .ExecuteScalarAsync(cancellationToken)
                        .ConfigureAwait(false);

                    if(result == null)
                    {
                        return Position.End;
                    }
                    return (long) result;
                }
            }
        }

        protected override async Task<int> ReadStreamHeadVersionInternal(string streamId, CancellationToken cancellationToken)
        {
            GuardAgainstDisposed();

            using(var connection = _createConnection())
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                using(var command = new SqlCommand(_scripts.ReadStreamHeadVersion, connection))
                {
                    command.CommandTimeout = _commandTimeout;
                    command.Parameters.Add(new SqlParameter("streamId", SqlDbType.Char, 42) { Value = new StreamIdInfo(streamId).SqlStreamId.Id });
                    var result = await command
                        .ExecuteScalarAsync(cancellationToken)
                        .ConfigureAwait(false);

                    if(result == null)
                    {
                        return StreamVersion.End;
                    }
                    return (int) result;
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if(disposing)
            {
                if(_streamStoreNotifier.IsValueCreated)
                {
                    _streamStoreNotifier.Value.Dispose();
                }
            }
            base.Dispose(disposing);
        }

        private IObservable<Unit> GetStoreObservable => _streamStoreNotifier.Value;

        /// <summary>
        /// Returns the script that can be used to create the Sql Stream Store in an MsSql database.
        /// </summary>
        /// <returns>The database creation script.</returns>
        public string GetSchemaCreationScript()
        {
            return _scripts.CreateSchema;
        }
    }
}
