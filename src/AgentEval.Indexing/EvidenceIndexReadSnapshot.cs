// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Results.Runs;
using Microsoft.Data.Sqlite;

namespace AgentEval.Indexing;

// Shared ownership and snapshot boundary for all index read families.
internal sealed class EvidenceIndexReadSnapshot(FileStream lease, SqliteConnection connection, SqliteTransaction transaction,
    string instanceId, long generation) : IDisposable
{
    internal SqliteConnection Connection { get; } = connection;
    internal SqliteTransaction Transaction { get; } = transaction;
    internal string InstanceId { get; } = instanceId;
    internal long Generation { get; } = generation;

    internal static EvidenceIndexReadSnapshot Open(EvidenceIndexOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(options.DatabasePath) || !Path.IsPathFullyQualified(options.DatabasePath) ||
            options.MaxDatabaseBytes is < (64 << 10) or > (64L << 30) || options.BusyTimeoutSeconds is < 1 or > 30)
            throw new EvidenceIndexException("invalid-read-options");
        var path = Path.GetFullPath(options.DatabasePath);
        new EvidenceIndexRootPlanner().Plan([new("index-parent", Path.GetDirectoryName(path)!, IndexedEvidenceKind.Aef)], cancellationToken: cancellationToken);
        if (!EvidenceIndexRebuildPreparation.Exists(path)) throw new EvidenceIndexException("missing-index");
        if (AefFolder.KindOf(path) != AefEntryKind.File || AefFolder.KindOf(path + ".lock") != AefEntryKind.File)
            throw new EvidenceIndexException("unsafe-path");
        var lease = new FileStream(path + ".lock", FileMode.Open, FileAccess.Read, FileShare.Read);
        SqliteConnection? connection = null; SqliteTransaction? transaction = null;
        try
        {
            SqliteEvidenceIndexSchema.CheckReadFiles(options);
            connection = SqliteEvidenceIndexSchema.Open(path, options.BusyTimeoutSeconds, readOnly: true);
            transaction = connection.BeginTransaction(deferred: true);
            var info = SqliteEvidenceIndexSchema.InspectReadSnapshot(options, connection, transaction, cancellationToken);
            return new(lease, connection, transaction, info.InstanceId, info.Generation);
        }
        catch { transaction?.Dispose(); connection?.Dispose(); lease.Dispose(); throw; }
    }

    public void Dispose() { Transaction.Dispose(); Connection.Dispose(); lease.Dispose(); }
}
