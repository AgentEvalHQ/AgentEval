// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MissionControl.Core;
using AgentEval.Results.Requirements;
using HotChocolate.CostAnalysis.Types;

namespace AgentEval.MissionControl.GraphQL;

/// <summary>Typed governance outcome over detached report records containing no storage locations.</summary>
public sealed record GovernanceStatusResponse(QueryState State, string? Code, RequirementStatusReport? Data);

public sealed partial class Query
{
    /// <summary>Computes host-configured requirements over explicit evidence selections and an exact view time.</summary>
    [Cost(500.0)]
    public GovernanceStatusResponse GovernanceStatus([Service] IGovernanceQueryService queries,
        string subjectRef, string subjectVersion, string[] sourceIds, string at, CancellationToken cancellationToken = default)
    {
        var result = queries.Status(new(subjectRef, subjectVersion), sourceIds, ParseViewTime(at), cancellationToken);
        return new(result.State, result.Code, result.Data);
    }
}
