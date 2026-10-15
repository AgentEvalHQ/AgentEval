// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using AgentEval.MissionControl.Core;
using HotChocolate.CostAnalysis.Types;

namespace AgentEval.MissionControl.GraphQL;

/// <summary>A source inventory outcome with the same typed availability semantics as in-process queries.</summary>
public sealed record EvidenceSourcesResponse(QueryState State, string? Code, EvidenceSourcesPageView? Data);
/// <summary>Detached source page; generation is decimal text to preserve all Int64 values in JavaScript clients.</summary>
public sealed record EvidenceSourcesPageView(string IndexInstanceId, string Generation,
    IReadOnlyList<EvidenceSourceView> Items, string? NextCursor);
/// <summary>Indexed provenance only. No physical binding directory or inferred evaluation verdict is exposed.</summary>
public sealed record EvidenceSourceView(string SourceId, string RootId, string RelativePath, string Kind,
    string Verification, string Fingerprint, string IndexedGeneration);

public sealed partial class Query
{
    /// <summary>Reads one bounded evidence-source page through the shared query service.</summary>
    [Cost(100.0)]
    public EvidenceSourcesResponse EvidenceSources([Service] IEvidenceQueryService queries,
        string? rootId = null, int pageSize = 100, string? cursor = null, CancellationToken cancellationToken = default)
    {
        var result = queries.Sources(new(rootId, pageSize, cursor), cancellationToken);
        var page = result.Data;
        return new(result.State, result.Code, page is null ? null : new(page.InstanceId,
            page.Generation.ToString(CultureInfo.InvariantCulture), page.Items.Select(item => new EvidenceSourceView(
                item.SourceId, item.RootId, item.RelativePath, item.Kind, item.Verification, item.Fingerprint,
                item.IndexedGeneration.ToString(CultureInfo.InvariantCulture))).ToArray(), page.NextCursor));
    }
}
