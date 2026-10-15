// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using AgentEval.Indexing;
using AgentEval.MissionControl.Core;
using HotChocolate.CostAnalysis.Types;

namespace AgentEval.MissionControl.GraphQL;

/// <summary>A typed outcome for one bounded indexed evidence page.</summary>
public sealed record EvidencePageResponse<T>(QueryState State, string? Code, EvidencePageView<T>? Data);
/// <summary>Index revision and source provenance accompany every detached page.</summary>
public sealed record EvidencePageView<T>(string IndexInstanceId, string Generation, string SourceKind,
    string Verification, string Fingerprint, IReadOnlyList<T> Items, string? NextCursor);
/// <summary>An indexed result, preserving aggregate case absence and exact trial integers.</summary>
public sealed record EvidenceResultView(string ResultId, string? CaseId, string? Trial, string? Outcome);
/// <summary>A metric occurrence; exceptional values and provenance remain separate from finite scores.</summary>
public sealed record EvidenceMetricView(string ResultId, string MetricId, int Ordinal, double? Score,
    string? ScoreText, bool? Passed, string Provenance);
/// <summary>Observed span identity and timestamps, without inferred evaluation outcomes.</summary>
public sealed record EvidenceSpanView(string ReportOrdinal, string GroupId, string GroupingRevision, string GroupingBasis,
    string MappingRevision, string TraceId, string SpanId, string ParentSpanId, string StartNanos, string EndNanos,
    string ResourceIndex, string ScopeIndex, string SpanIndex);
/// <summary>A normalized field with conflict state, exact integer text and original provenance JSON.</summary>
public sealed record EvidenceFieldView(string ReportOrdinal, string FieldName, string State, string? TextValue,
    string? IntegerValue, string SourcesJson);

public sealed partial class Query
{
    /// <summary>Reads indexed result rows for one selected source.</summary>
    [Cost(100.0)]
    public EvidencePageResponse<EvidenceResultView> EvidenceResults([Service] IEvidenceQueryService queries,
        string sourceId, int pageSize = 100, string? cursor = null, CancellationToken cancellationToken = default) =>
        MapPage(queries.Results(new(sourceId, pageSize, cursor), cancellationToken),
            item => new EvidenceResultView(item.ResultId, item.CaseId, item.Trial?.ToString(CultureInfo.InvariantCulture), item.Outcome));

    /// <summary>Reads indexed metric occurrences, including textual exceptional scores.</summary>
    [Cost(100.0)]
    public EvidencePageResponse<EvidenceMetricView> EvidenceMetrics([Service] IEvidenceQueryService queries,
        string sourceId, int pageSize = 100, string? cursor = null, CancellationToken cancellationToken = default) =>
        MapPage(queries.Metrics(new(sourceId, pageSize, cursor), cancellationToken),
            item => new EvidenceMetricView(item.ResultId, item.MetricId, item.Ordinal, item.Score, item.ScoreText, item.Passed, item.Provenance));

    /// <summary>Reads observed spans; all 64-bit values use decimal text.</summary>
    [Cost(100.0)]
    public EvidencePageResponse<EvidenceSpanView> EvidenceSpans([Service] IEvidenceQueryService queries,
        string sourceId, int pageSize = 100, string? cursor = null, CancellationToken cancellationToken = default) =>
        MapPage(queries.Spans(new(sourceId, pageSize, cursor), cancellationToken),
            item => new EvidenceSpanView(Decimal(item.ReportOrdinal), item.GroupId, item.GroupingRevision, item.GroupingBasis,
                item.MappingRevision, item.TraceId, item.SpanId, item.ParentSpanId, item.StartNanos, item.EndNanos,
                Decimal(item.ResourceIndex), Decimal(item.ScopeIndex), Decimal(item.SpanIndex)));

    /// <summary>Reads normalized observation fields without resolving conflicts or inventing consensus.</summary>
    [Cost(100.0)]
    public EvidencePageResponse<EvidenceFieldView> EvidenceFields([Service] IEvidenceQueryService queries,
        string sourceId, int pageSize = 100, string? cursor = null, CancellationToken cancellationToken = default) =>
        MapPage(queries.Fields(new(sourceId, pageSize, cursor), cancellationToken),
            item => new EvidenceFieldView(Decimal(item.ReportOrdinal), item.FieldName, item.State, item.TextValue,
                item.IntegerValue?.ToString(CultureInfo.InvariantCulture), item.SourcesJson));

    private static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static EvidencePageResponse<TView> MapPage<TSource, TView>(QueryResult<EvidenceDetailPage<TSource>> result,
        Func<TSource, TView> map)
    {
        var page = result.Data;
        return new(result.State, result.Code, page is null ? null : new(page.InstanceId, Decimal(page.Generation),
            page.SourceKind, page.Verification, page.Fingerprint, page.Items.Select(map).ToArray(), page.NextCursor));
    }
}
