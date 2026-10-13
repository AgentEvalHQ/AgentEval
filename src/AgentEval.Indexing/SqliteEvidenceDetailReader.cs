// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using Microsoft.Data.Sqlite;

namespace AgentEval.Indexing;

/// <summary>A bounded detail query for an exact indexed source ID.</summary>
public sealed record EvidenceDetailPageRequest(string SourceId, int PageSize = 100, string? Cursor = null);
/// <summary>A detached detail page with source provenance and index snapshot identity.</summary>
public sealed record EvidenceDetailPage<T>(string InstanceId, long Generation, string SourceKind, string Verification,
    string Fingerprint, ImmutableArray<T> Items, string? NextCursor);
/// <summary>One observed report; decimal nanosecond strings preserve the full unsigned 64-bit range.</summary>
public sealed record IndexedObservationSpan(long ReportOrdinal, string GroupId, string GroupingRevision, string GroupingBasis,
    string MappingRevision, string TraceId, string SpanId, string ParentSpanId, string StartNanos, string EndNanos,
    long ResourceIndex, long ScopeIndex, long SpanIndex);
/// <summary>A normalized observation field with exact integer value and original indexed provenance JSON.</summary>
public sealed record IndexedObservationField(long ReportOrdinal, string FieldName, string State, string? TextValue,
    long? IntegerValue, string SourcesJson);

/// <summary>Bounded source-scoped result/metric and observation report/field inventories.</summary>
public interface IEvidenceDetailReader
{
    /// <summary>Orders results by binary result ID. Null CaseId remains explicit for aggregate containers.</summary>
    EvidenceDetailPage<IndexedResult> ReadResults(EvidenceDetailPageRequest request, CancellationToken cancellationToken = default);
    /// <summary>Orders metric occurrences by binary result ID, metric ID, then numeric ordinal.</summary>
    EvidenceDetailPage<IndexedMetric> ReadMetrics(EvidenceDetailPageRequest request, CancellationToken cancellationToken = default);
    /// <summary>Orders observation reports by numeric report ordinal; no evaluation verdict is inferred.</summary>
    EvidenceDetailPage<IndexedObservationSpan> ReadSpans(EvidenceDetailPageRequest request, CancellationToken cancellationToken = default);
    /// <summary>Orders fields by numeric report ordinal then binary field name; conflict provenance is retained.</summary>
    EvidenceDetailPage<IndexedObservationField> ReadFields(EvidenceDetailPageRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Uses short shared read snapshots, exact primary-key continuation and finite row/text/cursor budgets.</summary>
public sealed class SqliteEvidenceDetailReader(EvidenceIndexOptions options) : IEvidenceDetailReader
{
    /// <inheritdoc />
    public EvidenceDetailPage<IndexedResult> ReadResults(EvidenceDetailPageRequest request, CancellationToken cancellationToken = default) =>
        Read(request, "results",
            r => new IndexedResult(r.GetString(0), Text(r, 1), Integer(r, 2), Text(r, 3)), r => new(r.ResultId, null, null), cancellationToken);

    /// <inheritdoc />
    public EvidenceDetailPage<IndexedMetric> ReadMetrics(EvidenceDetailPageRequest request, CancellationToken cancellationToken = default) =>
        Read(request, "metrics", r => new IndexedMetric(r.GetString(0), r.GetString(1), r.GetInt32(2),
                r.IsDBNull(3) ? null : r.GetDouble(3), Text(r, 4), r.IsDBNull(5) ? null : r.GetInt64(5) == 1, r.GetString(6)),
            r => new(r.ResultId, r.MetricId, r.Ordinal), cancellationToken);

    /// <inheritdoc />
    public EvidenceDetailPage<IndexedObservationSpan> ReadSpans(EvidenceDetailPageRequest request, CancellationToken cancellationToken = default) =>
        Read(request, "spans",
            r => new IndexedObservationSpan(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6),
                r.GetString(7), r.GetString(8), r.GetString(9), r.GetInt64(10), r.GetInt64(11), r.GetInt64(12)), r => new(null, null, r.ReportOrdinal), cancellationToken);

    /// <inheritdoc />
    public EvidenceDetailPage<IndexedObservationField> ReadFields(EvidenceDetailPageRequest request, CancellationToken cancellationToken = default) =>
        Read(request, "fields", r => new IndexedObservationField(r.GetInt64(0), r.GetString(1), r.GetString(2), Text(r, 3), Integer(r, 4), r.GetString(5)),
            r => new(r.FieldName, null, r.ReportOrdinal), cancellationToken);

    private EvidenceDetailPage<T> Read<T>(EvidenceDetailPageRequest request, string family, Func<SqliteDataReader, T> project, Func<T, PageKey> key, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request); token.ThrowIfCancellationRequested();
        if (request.SourceId is null || request.SourceId.Length != 64 || request.SourceId.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c)) ||
            request.PageSize is < 1 or > 500) throw new EvidenceIndexException("invalid-page-request");
        var cursor = Decode(request, family);
        try
        {
            using var snapshot = EvidenceIndexReadSnapshot.Open(options, token);
            if (cursor is not null && (cursor.InstanceId != snapshot.InstanceId || cursor.Generation != snapshot.Generation))
                throw new EvidenceIndexException("stale-cursor");
            string sourceKind; string verification; string fingerprint;
            using (var source = snapshot.Connection.CreateCommand())
            {
                source.Transaction = snapshot.Transaction; source.CommandText = "SELECT kind,verification,content_digest FROM sources WHERE source_id=$source";
                source.Parameters.AddWithValue("$source", request.SourceId);
                using var read = source.ExecuteReader();
                if (!read.Read()) throw new EvidenceIndexException("source-not-found");
                sourceKind = read.GetString(0); verification = read.GetString(1); fingerprint = read.GetString(2);
            }
            if ((family is "spans" or "fields") != (sourceKind == "observation")) throw new EvidenceIndexException("wrong-source-kind");
            object?[]? values = cursor is null ? null : family switch
            {
                "results" => [cursor.Key.First], "metrics" => [cursor.Key.First, cursor.Key.Second, cursor.Key.Ordinal],
                "spans" => [cursor.Key.Ordinal], _ => [cursor.Key.Ordinal, cursor.Key.First]
            };
            using var command = EvidenceIndexPageCommand.Details(snapshot.Connection, snapshot.Transaction,
                family, request.SourceId, request.PageSize, values);
            var columnCount = EvidenceIndexPageCommand.Shape(family).Columns.Length;
            var items = ImmutableArray.CreateBuilder<T>(); var more = false; long bytes = 0;
            using (var read = command.ExecuteReader())
            while (read.Read())
            {
                token.ThrowIfCancellationRequested();
                if (items.Count == request.PageSize) { more = true; break; }
                bytes += read.GetInt64(columnCount);
                if (bytes > 2 << 20) throw new EvidenceIndexException("page-byte-limit");
                items.Add(project(read));
            }
            token.ThrowIfCancellationRequested();
            var next = more ? Encode(new(1, SqliteEvidenceIndexSchema.CurrentVersion, snapshot.InstanceId, snapshot.Generation,
                family, request.SourceId, request.PageSize, key(items[^1]))) : null;
            return new(snapshot.InstanceId, snapshot.Generation, sourceKind, verification, fingerprint, items.ToImmutable(), next);
        }
        catch (SqliteException error) { throw new EvidenceIndexException(error.SqliteErrorCode is 5 or 6 ? "busy" : "read-failed"); }
    }

    private static string? Text(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static long? Integer(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    private sealed record PageKey(string? First, string? Second, long? Ordinal);
    private sealed record DetailCursor(int Version, int SchemaVersion, string InstanceId, long Generation, string Family,
        string SourceId, int PageSize, PageKey Key);
    private static string Encode(DetailCursor cursor) => EvidenceIndexCursor.Encode(cursor);
    private static DetailCursor? Decode(EvidenceDetailPageRequest request, string family)
    {
        if (request.Cursor is null) return null;
        var cursor = EvidenceIndexCursor.Decode<DetailCursor>(request.Cursor);
        if (cursor is null || cursor.Version != 1 || cursor.SchemaVersion != SqliteEvidenceIndexSchema.CurrentVersion ||
            !Guid.TryParseExact(cursor.InstanceId, "N", out _) || cursor.Generation < 0 || cursor.Family != family ||
            cursor.SourceId != request.SourceId || cursor.PageSize != request.PageSize || cursor.Key is null)
            throw new EvidenceIndexException("invalid-cursor");
        var k = cursor.Key;
        var valid = family switch
        {
            "results" => k.First is not null && k.Second is null && k.Ordinal is null,
            "metrics" => k.First is not null && k.Second is not null && k.Ordinal is >= 0 and <= int.MaxValue,
            "spans" => k.First is null && k.Second is null && k.Ordinal is >= 0,
            _ => k.First is not null && k.Second is null && k.Ordinal is >= 0
        };
        if (!valid) throw new EvidenceIndexException("invalid-cursor");
        return cursor;
    }
}
