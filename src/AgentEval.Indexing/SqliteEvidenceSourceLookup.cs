// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Data.Sqlite;

namespace AgentEval.Indexing;

/// <summary>
/// Server-side source selection and persisted location binding from one validated index snapshot.
/// Binding contains a physical directory and must not be exposed as a remote response or treated as authorization.
/// </summary>
public sealed record EvidenceSourceSelection(string InstanceId, long Generation, IndexedSourceEntry Source,
    IndexedRun? Run, EvidenceIndexRoot? Binding);

/// <summary>Exact source lookup for services that resolve configured evidence, without scanning inventory pages.</summary>
public interface IEvidenceSourceLookup
{
    /// <summary>Returns null only for an absent source. Unbound historical sources have a null Binding.</summary>
    EvidenceSourceSelection? Find(string sourceId, CancellationToken cancellationToken = default);
}

/// <summary>Reads source, evaluated-run metadata and optional root binding in one bounded, verified SQLite snapshot.</summary>
public sealed class SqliteEvidenceSourceLookup(EvidenceIndexOptions options) : IEvidenceSourceLookup
{
    /// <inheritdoc />
    public EvidenceSourceSelection? Find(string sourceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sourceId is null || sourceId.Length != 64 || sourceId.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c)))
            throw new EvidenceIndexException("invalid-page-request");
        try
        {
            using var snapshot = EvidenceIndexReadSnapshot.Open(options, cancellationToken);
            using var command = snapshot.Connection.CreateCommand(); command.Transaction = snapshot.Transaction;
            string[] columns = ["s.source_id", "s.root_id", "s.relative_path", "s.kind", "s.verification", "s.content_digest", "s.indexed_generation",
                "r.run_id", "r.run_hash", "r.subject_id", "r.suite_id", "r.suite_definition_digest", "r.started_at", "r.outcome", "r.completeness",
                "b.directory", "b.kind"];
            var lengths = string.Join('+', columns.Where((_, i) => i != 6).Select(c => $"coalesce(length(CAST({c} AS BLOB)),0)"));
            command.CommandText = $"SELECT {string.Join(',', columns)},{lengths} FROM sources s " +
                "LEFT JOIN evaluation_runs r ON r.source_id=s.source_id LEFT JOIN root_bindings b ON b.root_id=s.root_id WHERE s.source_id=$id";
            command.Parameters.AddWithValue("$id", sourceId);
            using var read = command.ExecuteReader();
            if (!read.Read()) return null;
            if (read.GetInt64(columns.Length) > 2 << 20) throw new EvidenceIndexException("page-byte-limit");
            var source = new IndexedSourceEntry(read.GetString(0), read.GetString(1), read.GetString(2), read.GetString(3),
                read.GetString(4), read.GetString(5), read.GetInt64(6));
            var run = read.IsDBNull(7) ? null : new IndexedRun(read.GetString(7), Text(8), Text(9), Text(10), Text(11), Text(12), Text(13), read.GetString(14));
            EvidenceIndexRoot? binding = null;
            if (!read.IsDBNull(15))
            {
                var kind = read.GetString(16);
                if (kind != source.Kind) throw new EvidenceIndexException("root-kind-mismatch");
                binding = new(source.RootId, read.GetString(15), kind switch
                {
                    "aef" => IndexedEvidenceKind.Aef, "legacy" => IndexedEvidenceKind.Legacy,
                    "observation" => IndexedEvidenceKind.Observation, _ => throw new EvidenceIndexException("invalid-source-kind")
                });
            }
            if ((source.Kind == "observation") != (run is null)) throw new EvidenceIndexException("invalid-source-projection");
            cancellationToken.ThrowIfCancellationRequested();
            return new(snapshot.InstanceId, snapshot.Generation, source, run, binding);
            string? Text(int i) => read.IsDBNull(i) ? null : read.GetString(i);
        }
        catch (SqliteException error) { throw new EvidenceIndexException(error.SqliteErrorCode is 5 or 6 ? "busy" : "read-failed"); }
    }
}
