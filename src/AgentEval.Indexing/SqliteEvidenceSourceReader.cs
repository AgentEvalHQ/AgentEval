// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using Microsoft.Data.Sqlite;

namespace AgentEval.Indexing;

/// <summary>A bounded source inventory request, ordered by binary root ID then relative path.</summary>
public sealed record EvidenceSourcePageRequest(string? RootId = null, int PageSize = 100, string? Cursor = null);
/// <summary>Index provenance for one source; does not claim freshness, completeness or evaluation success.</summary>
public sealed record IndexedSourceEntry(string SourceId, string RootId, string RelativePath, string Kind,
    string Verification, string Fingerprint, long IndexedGeneration);
/// <summary>A detached page and an optional continuation bound to the same index instance/generation/query.</summary>
public sealed record EvidenceSourcePage(string InstanceId, long Generation, ImmutableArray<IndexedSourceEntry> Items, string? NextCursor);
/// <summary>Reads finite pages without maintaining a database connection between requests.</summary>
public interface IEvidenceSourceReader
{
    /// <summary>Refuses stale cursors. Page size is 1..500; total text payload is limited to 2 MiB.</summary>
    EvidenceSourcePage Read(EvidenceSourcePageRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Uses a shared replacement lease and a read-only SQLite snapshot for each page. Cursor bytes are
/// bounded canonical JSON encoded as base64url, not an authorization token or integrity attestation.
/// </summary>
public sealed class SqliteEvidenceSourceReader(EvidenceIndexOptions options) : IEvidenceSourceReader
{
    private const long MaxPageTextBytes = 2 << 20;

    /// <inheritdoc />
    public EvidenceSourcePage Read(EvidenceSourcePageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.PageSize is < 1 or > 500 || request.RootId is { } root &&
            (string.IsNullOrWhiteSpace(root) || root.Length > 1024 || root.Any(char.IsControl)))
            throw new EvidenceIndexException("invalid-page-request");
        var cursor = Decode(request);
        try
        {
            using var snapshot = EvidenceIndexReadSnapshot.Open(options, cancellationToken);
            var connection = snapshot.Connection; var transaction = snapshot.Transaction;
            var instance = snapshot.InstanceId; var generation = snapshot.Generation;
            if (cursor is not null && (cursor.InstanceId != instance || cursor.Generation != generation))
                throw new EvidenceIndexException("stale-cursor");
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                SELECT source_id,root_id,relative_path,kind,verification,content_digest,indexed_generation,
                    length(CAST(source_id AS BLOB))+length(CAST(root_id AS BLOB))+length(CAST(relative_path AS BLOB))+
                    length(CAST(kind AS BLOB))+length(CAST(verification AS BLOB))+length(CAST(content_digest AS BLOB))
                FROM sources
                """;
            var conditions = new List<string>();
            if (request.RootId is not null)
            {
                conditions.Add("root_id=$root COLLATE BINARY"); command.Parameters.AddWithValue("$root", request.RootId);
            }
            if (cursor is not null)
            {
                conditions.Add("(root_id,relative_path)>($lastRoot,$lastPath)");
                command.Parameters.AddWithValue("$lastRoot", cursor.LastRoot); command.Parameters.AddWithValue("$lastPath", cursor.LastPath);
            }
            if (conditions.Count > 0) command.CommandText += " WHERE " + string.Join(" AND ", conditions);
            command.CommandText += " ORDER BY root_id COLLATE BINARY,relative_path COLLATE BINARY LIMIT $limit";
            command.Parameters.AddWithValue("$limit", request.PageSize + 1);
            var items = ImmutableArray.CreateBuilder<IndexedSourceEntry>(); long textBytes = 0; var more = false;
            using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (items.Count == request.PageSize) { more = true; break; }
                textBytes += reader.GetInt64(7);
                if (textBytes > MaxPageTextBytes) throw new EvidenceIndexException("page-byte-limit");
                items.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetString(5), reader.GetInt64(6)));
            }
            cancellationToken.ThrowIfCancellationRequested();
            var next = more ? Encode(new(1, SqliteEvidenceIndexSchema.CurrentVersion, instance, generation, request.RootId,
                request.PageSize, items[^1].RootId, items[^1].RelativePath)) : null;
            return new(instance, generation, items.ToImmutable(), next);
        }
        catch (SqliteException error) { throw new EvidenceIndexException(error.SqliteErrorCode is 5 or 6 ? "busy" : "read-failed"); }
    }

    private static SourceCursor? Decode(EvidenceSourcePageRequest request)
    {
        if (request.Cursor is null) return null;
        var cursor = EvidenceIndexCursor.Decode<SourceCursor>(request.Cursor);
        if (cursor is null || cursor.Version != 1 || cursor.SchemaVersion != SqliteEvidenceIndexSchema.CurrentVersion ||
            !Guid.TryParseExact(cursor.InstanceId, "N", out _) || cursor.Generation < 0 ||
            cursor.RootId != request.RootId || cursor.PageSize != request.PageSize ||
            string.IsNullOrWhiteSpace(cursor.LastRoot) || cursor.LastRoot.Length > 1024 ||
            string.IsNullOrWhiteSpace(cursor.LastPath) || cursor.LastPath.Length > 4096 ||
            cursor.RootId is not null && cursor.LastRoot != cursor.RootId)
            throw new EvidenceIndexException("invalid-cursor");
        return cursor;
    }
    private static string Encode(SourceCursor cursor) => EvidenceIndexCursor.Encode(cursor);
    private sealed record SourceCursor(int Version, int SchemaVersion, string InstanceId, long Generation, string? RootId,
        int PageSize, string LastRoot, string LastPath);
}
