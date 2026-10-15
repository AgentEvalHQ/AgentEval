// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using AgentEval.Indexing;
using AgentEval.MissionControl.Core;
using HotChocolate.CostAnalysis.Types;

namespace AgentEval.MissionControl.GraphQL;

/// <summary>Bounded configured checkpoint declarations; listing does not verify or approve them.</summary>
public sealed record CheckpointsResponse(QueryState State, string? Code, IReadOnlyList<CheckpointDescriptor>? Data);
/// <summary>Typed checkpoint explanation with no partial evidence on refusal.</summary>
public sealed record CheckpointExplanationResponse(QueryState State, string? Code, CheckpointSelectionView? Data);
/// <summary>Explicit selection and exact index revision, absent for a selection without indexed runs.</summary>
public sealed record CheckpointSelectionView(string CheckpointId, IReadOnlyList<string> SourceIds,
    string? IndexInstanceId, string? IndexGeneration, CheckpointExplanation Explanation);

public sealed partial class Query
{
    /// <summary>Lists the bounded host-configured checkpoint descriptors.</summary>
    [Cost(100.0)]
    public CheckpointsResponse Checkpoints([Service] ICheckpointQueryService queries, CancellationToken cancellationToken = default)
    {
        var result = queries.List(cancellationToken);
        return new(result.State, result.Code, result.Data);
    }

    /// <summary>Verifies a configured checkpoint with explicit evidence under the host's trust policy.</summary>
    [Cost(500.0)]
    public CheckpointExplanationResponse CheckpointExplanation([Service] ICheckpointQueryService queries,
        string checkpointId, string[] sourceIds, string at, CancellationToken cancellationToken = default)
    {
        var result = queries.Explain(checkpointId, sourceIds, ParseViewTime(at), cancellationToken);
        var data = result.Data;
        return new(result.State, result.Code, data is null ? null : new(data.CheckpointId, data.SourceIds,
            data.IndexInstanceId, data.IndexGeneration?.ToString(CultureInfo.InvariantCulture), data.Explanation));
    }
}
