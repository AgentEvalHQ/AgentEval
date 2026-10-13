// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentEval.Results.Observations;
using Microsoft.Data.Sqlite;

namespace AgentEval.Indexing;

/// <summary>
/// Single-owner transactional writer. Materializes only bounded immutable projections, preserves
/// previous rows on failure and never reads, edits or deletes authoritative source files.
/// </summary>
public sealed class SqliteEvidenceIndexWriter : IEvidenceIndexWriter
{
    private readonly object _gate = new();
    private readonly EvidenceIndexOptions _options;
    private readonly EvidenceIndexWriteLimits _limits;
    private readonly SqliteConnection _connection;
    private readonly FileStream _lease;
    private bool _disposed;
    private static readonly string[] SourceTables = ["sources", "evaluation_runs", "evaluation_results", "metric_values", "observation_batches", "observed_spans", "observed_fields"];

    /// <summary>Validates schema and acquires exclusive writer ownership. The injected schema service remains authoritative for compatibility.</summary>
    public SqliteEvidenceIndexWriter(EvidenceIndexOptions options, IEvidenceIndexSchema schema,
        EvidenceIndexWriteLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(schema);
        _limits = limits ?? new();
        if (_limits.MaxRowsPerSource is < 1 or > 1_000_000 || _limits.MaxTextBytesPerSource is < 1 or > (64 << 20)
            || _limits.MaxSources is < 1 or > 1_000_000 || _limits.MaxTotalRows is < 1 or > 10_000_000)
            throw new ArgumentException("Expected: finite source/text/row budgets. Actual: invalid writer limits. Suggestions: use documented defaults. Because: index growth must be bounded.", nameof(limits));
        schema.EnsureCurrent(options, cancellationToken);
        _options = options;
        var path = Path.GetFullPath(options.DatabasePath);
        _lease = new FileStream(path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        SqliteConnection? opened = null;
        try
        {
            opened = SqliteEvidenceIndexSchema.Open(path, options.BusyTimeoutSeconds);
            using var command = opened.CreateCommand(); command.CommandText = "PRAGMA page_size";
            var pageSize = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            command.CommandText = "PRAGMA max_page_count=" + (options.MaxDatabaseBytes / pageSize).ToString(CultureInfo.InvariantCulture);
            command.ExecuteScalar();
            _connection = opened;
        }
        catch { opened?.Dispose(); _lease.Dispose(); throw; }
    }

    /// <inheritdoc />
    public EvidenceIndexMutation Upsert(EvidenceIndexSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        Enter(cancellationToken);
        try
        {
            var id = Identity(source.RootId, source.RelativePath);
            if (!DigestValid(source.ContentDigest)) throw new EvidenceIndexException("invalid-source-digest");
            var kind = source.Kind switch { IndexedEvidenceKind.Aef => "aef", IndexedEvidenceKind.Legacy => "legacy", IndexedEvidenceKind.Observation => "observation", _ => throw new EvidenceIndexException("invalid-source-kind") };
            var verification = source.Verification switch { IndexedVerification.Unverified => "unverified", IndexedVerification.Intact => "intact", IndexedVerification.Invalid => "invalid", _ => throw new EvidenceIndexException("invalid-verification") };
            if (kind != "aef" && verification != "unverified") throw new EvidenceIndexException("invalid-verification");
            var rows = Project(source, cancellationToken);
            // Include the actual projected rows, source digest and verification in the checkpoint. A changed
            // adapter projection cannot be silently skipped just because the upstream file digest is unchanged.
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long textBytes = 0;
            HashValue("index-projection/1"); HashValue(id); HashValue(source.RootId); HashValue(source.RelativePath);
            HashValue(source.ContentDigest); HashValue(kind); HashValue(verification);
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                HashValue(row.Table);
                foreach (var value in row.Values) HashValue(value);
            }
            var fingerprint = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            using var transaction = _connection.BeginTransaction();
            var generation = Scalar(transaction, "SELECT generation FROM index_metadata WHERE singleton=1");
            var existing = FindCore(id, transaction);
            if (existing?.Fingerprint == fingerprint) return new(id, fingerprint, generation, false);
            if (generation == long.MaxValue) throw new EvidenceIndexException("generation-limit");
            if (existing is null && Scalar(transaction, "SELECT count(*) FROM sources") >= _limits.MaxSources)
                throw new EvidenceIndexException("source-limit");
            Execute(transaction, "DELETE FROM sources WHERE source_id=$p0", id);
            Execute(transaction, "INSERT INTO sources VALUES ($p0,$p1,$p2,$p3,$p4,$p5,$p6)",
                id, source.RootId, source.RelativePath, kind, fingerprint, generation + 1, verification);
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var values = new object?[row.Values.Length + 1]; values[0] = id; row.Values.CopyTo(values, 1);
                var parameters = string.Join(',', Enumerable.Range(0, values.Length).Select(i => "$p" + i));
                Execute(transaction, $"INSERT INTO {row.Table} VALUES ({parameters})", values);
            }
            CheckBudgets(transaction);
            Execute(transaction, "UPDATE index_metadata SET generation=$p0 WHERE singleton=1", generation + 1);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return new(id, fingerprint, generation + 1, true);

            void HashValue(object? value)
            {
                var type = value switch { null => "null", string => "text", double => "real", _ => "integer" };
                var text = value switch { null => "", double real => real.ToString("R", CultureInfo.InvariantCulture), _ => Convert.ToString(value, CultureInfo.InvariantCulture)! };
                textBytes += Encoding.UTF8.GetByteCount(text);
                if (textBytes > _limits.MaxTextBytesPerSource) throw new EvidenceIndexException("projection-byte-limit");
                var bytes = Encoding.UTF8.GetBytes(text);
                hash.AppendData(Encoding.ASCII.GetBytes(type + ":" + bytes.Length.ToString(CultureInfo.InvariantCulture) + ":"));
                hash.AppendData(bytes);
            }
        }
        catch (SqliteException error) { throw SqlFailure(error); }
        finally { Monitor.Exit(_gate); }
    }

    /// <inheritdoc />
    public EvidenceIndexCheckpoint? Find(string rootId, string relativePath, CancellationToken cancellationToken = default)
    {
        Enter(cancellationToken);
        try { return FindCore(Identity(rootId, relativePath), null); }
        catch (SqliteException error) { throw SqlFailure(error); }
        finally { Monitor.Exit(_gate); }
    }

    /// <inheritdoc />
    public EvidenceIndexMutation Remove(string rootId, string relativePath, string expectedFingerprint, CancellationToken cancellationToken = default)
    {
        Enter(cancellationToken);
        try
        {
            var id = Identity(rootId, relativePath);
            if (!DigestValid(expectedFingerprint)) throw new EvidenceIndexException("invalid-checkpoint");
            using var transaction = _connection.BeginTransaction();
            var generation = Scalar(transaction, "SELECT generation FROM index_metadata WHERE singleton=1");
            var existing = FindCore(id, transaction);
            if (existing is null) return new(id, expectedFingerprint, generation, false);
            if (existing.Fingerprint != expectedFingerprint) throw new EvidenceIndexException("stale-checkpoint");
            if (generation == long.MaxValue) throw new EvidenceIndexException("generation-limit");
            Execute(transaction, "DELETE FROM sources WHERE source_id=$p0", id);
            Execute(transaction, "UPDATE index_metadata SET generation=$p0 WHERE singleton=1", generation + 1);
            cancellationToken.ThrowIfCancellationRequested(); transaction.Commit();
            return new(id, expectedFingerprint, generation + 1, true);
        }
        catch (SqliteException error) { throw SqlFailure(error); }
        finally { Monitor.Exit(_gate); }
    }

    private sealed record Row(string Table, object?[] Values);
    private List<Row> Project(EvidenceIndexSource source, CancellationToken token)
    {
        var rows = new List<Row>();
        long textBytes = 0;
        if (source.Results.IsDefault || source.Metrics.IsDefault) throw new EvidenceIndexException("invalid-projection");
        if ((long)source.Results.Length + source.Metrics.Length + 2 > _limits.MaxRowsPerSource)
            throw new EvidenceIndexException("source-row-limit");
        if (source.Kind == IndexedEvidenceKind.Observation)
        {
            if (source.Run is not null || !source.Results.IsEmpty || !source.Metrics.IsEmpty || source.Observation is not { } observation
                || observation.Sessions.IsDefault || observation.Content is null) throw new EvidenceIndexException("invalid-projection");
            Add("observation_batches", "observation", observation.Source, observation.Content.Capture.ToString(), observation.Content.Revision, JsonSerializer.Serialize(observation.Content.Removals));
            var ordinal = 0;
            foreach (var session in observation.Sessions)
            foreach (var member in session.Members)
            {
                var span = member.Span;
                Add("observed_spans", ordinal, session.Id, session.Revision, member.Basis.ToString(), span.MappingRevision,
                    span.TraceId, span.SpanId, span.ParentSpanId, span.StartTimeUnixNano.ToString("D20", CultureInfo.InvariantCulture),
                    span.EndTimeUnixNano.ToString("D20", CultureInfo.InvariantCulture), span.ResourceIndex, span.ScopeIndex, span.SpanIndex);
                foreach (var pair in span.Fields.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    var field = pair.Value;
                    if (field.Value is not (null or OtlpValue.Text or OtlpValue.Integer)) throw new EvidenceIndexException("invalid-normalized-value");
                    if ((field.State == OtlpFieldState.Present) != (field.Value is not null)) throw new EvidenceIndexException("invalid-normalized-value");
                    long provenanceBytes = 0;
                    foreach (var origin in field.Sources)
                    {
                        token.ThrowIfCancellationRequested();
                        if (origin.Value is not (null or OtlpValue.Text or OtlpValue.Integer)) throw new EvidenceIndexException("invalid-normalized-value");
                        foreach (var text in new[] { origin.Location, origin.Attribute, origin.Profile, origin.Issue, (origin.Value as OtlpValue.Text)?.Value })
                            if (text is not null) provenanceBytes += Encoding.UTF8.GetByteCount(text);
                        if (provenanceBytes > _limits.MaxTextBytesPerSource - textBytes) throw new EvidenceIndexException("projection-byte-limit");
                    }
                    var provenance = JsonSerializer.Serialize(field.Sources.Select(s => new
                    {
                        s.Location, s.Attribute, s.Profile, s.Issue,
                        Text = (s.Value as OtlpValue.Text)?.Value, Integer = (s.Value as OtlpValue.Integer)?.Value
                    }));
                    Add("observed_fields", ordinal, pair.Key, field.State.ToString(), (field.Value as OtlpValue.Text)?.Value,
                        (field.Value as OtlpValue.Integer)?.Value, provenance);
                }
                ordinal++;
            }
        }
        else
        {
            if (source.Observation is not null || source.Run is not { } run) throw new EvidenceIndexException("invalid-projection");
            Add("evaluation_runs", source.Kind == IndexedEvidenceKind.Aef ? "aef" : "legacy", run.RunId, run.RunHash,
                run.SubjectId, run.SuiteId, run.SuiteDefinitionDigest, run.StartedAt, run.Outcome, run.Completeness);
            foreach (var result in source.Results.OrderBy(r => r.ResultId, StringComparer.Ordinal))
                Add("evaluation_results", result.ResultId, result.CaseId, result.Trial, result.Outcome);
            foreach (var metric in source.Metrics.OrderBy(m => m.ResultId, StringComparer.Ordinal).ThenBy(m => m.MetricId, StringComparer.Ordinal).ThenBy(m => m.Ordinal))
            {
                if (metric.Score is { } score && !double.IsFinite(score)) throw new EvidenceIndexException("nonfinite-score");
                Add("metric_values", metric.ResultId, metric.MetricId, metric.Ordinal, metric.Score, metric.ScoreText,
                    metric.Passed.HasValue ? metric.Passed.Value ? 1 : 0 : null, metric.Provenance);
            }
        }
        return rows;

        void Add(string table, params object?[] values)
        {
            token.ThrowIfCancellationRequested();
            if (rows.Count + 1 >= _limits.MaxRowsPerSource) throw new EvidenceIndexException("source-row-limit");
            foreach (var value in values)
                if (value is string text) textBytes += Encoding.UTF8.GetByteCount(text);
            if (textBytes > _limits.MaxTextBytesPerSource) throw new EvidenceIndexException("projection-byte-limit");
            rows.Add(new(table, values));
        }
    }

    private EvidenceIndexCheckpoint? FindCore(string id, SqliteTransaction? transaction)
    {
        using var command = _connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT content_digest,indexed_generation,kind FROM sources WHERE source_id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(id, reader.GetString(0), reader.GetInt64(1), reader.GetString(2)) : null;
    }
    private void CheckBudgets(SqliteTransaction transaction)
    {
        long rows = 0;
        foreach (var table in SourceTables) rows += Scalar(transaction, $"SELECT count(*) FROM {table}");
        if (rows > _limits.MaxTotalRows) throw new EvidenceIndexException("total-row-limit");
        var pages = Scalar(transaction, "PRAGMA page_count"); var pageSize = Scalar(transaction, "PRAGMA page_size");
        if (pages > _options.MaxDatabaseBytes / pageSize) throw new EvidenceIndexException("size-limit");
    }
    private void Execute(SqliteTransaction transaction, string sql, params object?[] values)
    {
        using var command = _connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue("$p" + i, values[i] ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
    private long Scalar(SqliteTransaction transaction, string sql)
    {
        using var command = _connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
    private static string Identity(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(root) || root.Length > 1024 || string.IsNullOrWhiteSpace(path) || path.Length > 4096
            || path.StartsWith('/') || path.Contains('\\') || path.Contains(':') || path.Split('/').Any(p => p is "" or ".." || p == "." && path != ".")
            || path.Any(char.IsControl) || root.Any(char.IsControl)) throw new EvidenceIndexException("invalid-source-identity");
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { root, path }))).ToLowerInvariant();
    }
    private static bool DigestValid(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static EvidenceIndexException SqlFailure(SqliteException error) => new(error.SqliteErrorCode switch { 5 or 6 => "busy", 19 => "invalid-projection", 13 => "size-limit", _ => "write-failed" });
    private void Enter(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Monitor.TryEnter(_gate)) throw new EvidenceIndexException("busy");
        if (_disposed) { Monitor.Exit(_gate); throw new ObjectDisposedException(nameof(SqliteEvidenceIndexWriter)); }
    }
    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _connection.Dispose(); _lease.Dispose(); }
    }
}
