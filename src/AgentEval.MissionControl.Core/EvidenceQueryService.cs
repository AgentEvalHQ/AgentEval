// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Indexing;

namespace AgentEval.MissionControl.Core;

/// <summary>Shared bounded evidence queries for in-process consumers and remote transport adapters.</summary>
public interface IEvidenceQueryService
{
    /// <summary>Reads one source page. Does not infer source freshness or automatically follow continuations.</summary>
    QueryResult<EvidenceSourcePage> Sources(EvidenceSourcePageRequest request, CancellationToken cancellationToken = default);
    /// <summary>Reads one result page, retaining aggregate rows with null case IDs.</summary>
    QueryResult<EvidenceDetailPage<IndexedResult>> Results(EvidenceDetailPageRequest request, CancellationToken cancellationToken = default);
    /// <summary>Reads one metric page, retaining ordinal, exact/textual values and provenance.</summary>
    QueryResult<EvidenceDetailPage<IndexedMetric>> Metrics(EvidenceDetailPageRequest request, CancellationToken cancellationToken = default);
    /// <summary>Reads one observation report page; telemetry never becomes an evaluation verdict.</summary>
    QueryResult<EvidenceDetailPage<IndexedObservationSpan>> Spans(EvidenceDetailPageRequest request, CancellationToken cancellationToken = default);
    /// <summary>Reads one normalized field page, preserving conflict state and provenance.</summary>
    QueryResult<EvidenceDetailPage<IndexedObservationField>> Fields(EvidenceDetailPageRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Composes index readers without transport dependencies or implicit multi-page work. Hosts enforce access
/// policy before invoking this service; a root filter or cursor is not an authorization mechanism.
/// </summary>
public sealed class EvidenceQueryService : IEvidenceQueryService
{
    private readonly IEvidenceSourceReader? _sources;
    private readonly IEvidenceDetailReader? _details;

    /// <summary>Creates an explicitly unconfigured capability; no directory or index is inferred or created.</summary>
    public EvidenceQueryService() { }

    /// <summary>Creates a configured capability from injected bounded readers.</summary>
    public EvidenceQueryService(IEvidenceSourceReader sources, IEvidenceDetailReader details)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _details = details ?? throw new ArgumentNullException(nameof(details));
    }

    /// <inheritdoc />
    public QueryResult<EvidenceSourcePage> Sources(EvidenceSourcePageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Read(() => _sources!.Read(request, cancellationToken), page => page.Items.IsEmpty, cancellationToken);
    }
    /// <inheritdoc />
    public QueryResult<EvidenceDetailPage<IndexedResult>> Results(EvidenceDetailPageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Read(() => _details!.ReadResults(request, cancellationToken), page => page.Items.IsEmpty, cancellationToken);
    }
    /// <inheritdoc />
    public QueryResult<EvidenceDetailPage<IndexedMetric>> Metrics(EvidenceDetailPageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Read(() => _details!.ReadMetrics(request, cancellationToken), page => page.Items.IsEmpty, cancellationToken);
    }
    /// <inheritdoc />
    public QueryResult<EvidenceDetailPage<IndexedObservationSpan>> Spans(EvidenceDetailPageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Read(() => _details!.ReadSpans(request, cancellationToken), page => page.Items.IsEmpty, cancellationToken);
    }
    /// <inheritdoc />
    public QueryResult<EvidenceDetailPage<IndexedObservationField>> Fields(EvidenceDetailPageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Read(() => _details!.ReadFields(request, cancellationToken), page => page.Items.IsEmpty, cancellationToken);
    }

    private QueryResult<T> Read<T>(Func<T> read, Func<T, bool> empty, CancellationToken token) where T : class
    {
        token.ThrowIfCancellationRequested();
        if (_sources is null) return new(QueryState.NotConfigured, null, "not-configured");
        try
        {
            var page = read(); token.ThrowIfCancellationRequested();
            return new(empty(page) ? QueryState.Empty : QueryState.Available, page, null);
        }
        catch (EvidenceIndexException error)
        {
            // Never forward arbitrary exception codes supplied by an adapter.
            var (state, code) = error.Code switch
            {
                "source-not-found" => (QueryState.NotFound, "source-not-found"),
                "invalid-page-request" => (QueryState.InvalidRequest, "invalid-page-request"),
                "invalid-cursor" => (QueryState.InvalidRequest, "invalid-cursor"),
                "wrong-source-kind" => (QueryState.InvalidRequest, "wrong-source-kind"),
                "stale-cursor" => (QueryState.StaleCursor, "stale-cursor"),
                "page-byte-limit" => (QueryState.ResourceLimit, "page-byte-limit"),
                "size-limit" => (QueryState.ResourceLimit, "index-size-limit"),
                "missing-index" => (QueryState.Unavailable, "missing-index"),
                "busy" => (QueryState.Unavailable, "index-busy"),
                _ => (QueryState.Unavailable, "index-unavailable")
            };
            return new(state, null, code);
        }
        catch (IOException) { return new(QueryState.Unavailable, null, "index-unavailable"); }
        catch (UnauthorizedAccessException) { return new(QueryState.Unavailable, null, "index-unavailable"); }
    }
}
