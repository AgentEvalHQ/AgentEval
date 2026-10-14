// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MissionControl.Core;
using HotChocolate.CostAnalysis.Types;

namespace AgentEval.MissionControl.GraphQL;

/// <summary>Verified suite identity outcome; refusals carry no evidence data.</summary>
public sealed record SuiteIdentityResponse(QueryState State, string? Code, SuiteIdentityView? Data);
/// <summary>Content-free sealed identity and selection revision, without physical storage paths.</summary>
public sealed record SuiteIdentityView(string SourceId, string IndexInstanceId, string IndexGeneration,
    string RunHash, string RunId, string RunStatus, string Scope, string? SuiteRef, string? SuiteVersion,
    string? ContentDigest, string? DefinitionDigest, bool? Frozen);
/// <summary>A strict identity comparison outcome, not statistical eligibility or a score verdict.</summary>
public sealed record SuiteComparisonResponse(QueryState State, string? Code, SuiteComparisonView? Data);
/// <summary>Comparison explanation and the exact verified evidence used on both sides.</summary>
public sealed record SuiteComparisonView(bool Compatible, string Reason, string Description,
    SuiteIdentityView Baseline, SuiteIdentityView Candidate);

public sealed partial class Query
{
    /// <summary>Re-verifies suite identity for an explicit source under host-configured roots.</summary>
    [Cost(250.0)]
    public SuiteIdentityResponse EvidenceSuite([Service] ISuiteQueryService queries, string sourceId,
        CancellationToken cancellationToken = default)
    {
        var result = queries.Identity(sourceId, cancellationToken);
        return new(result.State, result.Code, result.Data is null ? null : SuiteView(result.Data));
    }

    /// <summary>Compares sealed suite definitions selected at one index revision.</summary>
    [Cost(500.0)]
    public SuiteComparisonResponse EvidenceSuiteComparison([Service] ISuiteQueryService queries,
        string baselineSourceId, string candidateSourceId, CancellationToken cancellationToken = default)
    {
        var result = queries.Compare(baselineSourceId, candidateSourceId, cancellationToken);
        var data = result.Data;
        return new(result.State, result.Code, data is null ? null : new(data.Compatibility.Compatible,
            data.Compatibility.Reason, data.Compatibility.Description, SuiteView(data.Baseline), SuiteView(data.Candidate)));
    }

    private static SuiteIdentityView SuiteView(VerifiedSuiteEvidence evidence)
    {
        var identity = evidence.Identity;
        return new(evidence.SourceId, evidence.IndexInstanceId, Decimal(evidence.IndexGeneration), identity.RunHash,
            identity.RunId, identity.RunStatus, identity.Scope, identity.SuiteRef, identity.SuiteVersion,
            identity.ContentDigest, identity.Definition?.Digest, identity.Definition?.Frozen);
    }
}
