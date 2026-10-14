// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MissionControl.Core;
using AgentEval.Results;
using HotChocolate.CostAnalysis.Types;

namespace AgentEval.MissionControl.GraphQL;

/// <summary>Verified detail outcome with no partial evidence on refusal.</summary>
public sealed record EvidenceResultDetailResponse(QueryState State, string? Code, EvidenceResultDetailView? Data);
/// <summary>Sealed result content alongside effective overlay state and its verification provenance.</summary>
public sealed record EvidenceResultDetailView(string SourceId, string IndexInstanceId, string IndexGeneration,
    string RunHash, string SnapshotDigest, string PolicyDigest, string At, string ResultId, string SealedJson,
    string SealedState, string EffectiveState, string? OverrideEvent, IReadOnlyList<EvidenceReviewView> Reviews,
    IReadOnlyList<EvidenceAssuranceView> Assurance, int UnsealedEvents);
/// <summary>A review target, status and originating event.</summary>
public sealed record EvidenceReviewView(string Target, string Status, string Event);
/// <summary>Assurance actually established for a displayed event, not its unverified claim.</summary>
public sealed record EvidenceAssuranceView(string Event, string Shown);

public sealed partial class Query
{
    /// <summary>Re-verifies one selected result and computes its effective state at an explicit UTC instant.</summary>
    [Cost(250.0)]
    public EvidenceResultDetailResponse EvidenceResultDetail([Service] IResultDetailQueryService queries,
        string sourceId, string resultId, string at, CancellationToken cancellationToken = default)
    {
        var result = queries.Result(sourceId, resultId, ParseViewTime(at), cancellationToken);
        var selected = result.Data;
        var detail = selected?.Result;
        return new(result.State, result.Code, selected is null || detail is null ? null : new(selected.SourceId,
            selected.IndexInstanceId, Decimal(selected.IndexGeneration), detail.RunHash, detail.SnapshotDigest,
            detail.PolicyDigest, detail.At, detail.ResultId, detail.SealedJson, detail.SealedState, detail.EffectiveState,
            detail.OverrideEvent, detail.Reviews.Select(r => new EvidenceReviewView(r.Target, r.Status, r.Event)).ToArray(),
            detail.Assurance.Select(a => new EvidenceAssuranceView(a.Event, a.Shown)).ToArray(), detail.UnsealedEvents));
    }

    private static AefTime ParseViewTime(string value)
    {
        // Invalid sentinel lets the shared service own typed rejection, including unconfigured precedence.
        if (value.Length is < 20 or > 30) return new(0, -1);
        try { return AefTime.Parse(value); }
        catch (FormatException) { return new(0, -1); }
    }
}
