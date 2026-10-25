// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Results.Adapters.Reading;

namespace AgentEval.MissionControl.GraphQL;

/// <summary>All recorded nodes of one case, including trials and final roots, from one verified source.</summary>
public sealed record WorkspaceResultTree(string SourceId, string RunId, string CaseId, string? SelectedResultId,
    string ContentCapture, AefRunReadInfo Evidence, IReadOnlyList<string> RootResultIds,
    IReadOnlyList<WorkspaceMetricDefinition> Metrics, IReadOnlyList<WorkspaceResultNode> Nodes,
    IReadOnlyList<WorkspaceArtifact> Artifacts);

/// <summary>A score's declared meaning. Bounds and units are never inferred from observed values.</summary>
public sealed record WorkspaceMetricDefinition(string Id, string Kind, string Direction, string Scale,
    double? Minimum, double? Maximum, string? Unit, string? Description, string RecordedJson);

/// <summary>A complete canonical result with convenient typed views; recorded JSON retains extensions and unfamiliar fields.</summary>
public sealed record WorkspaceResultNode(string ResultId, string? ParentResultId, string CaseId, string Path,
    int? Trial, string EvaluatorId, string? EvaluatorVersion, string State, string? Reason, string? Severity,
    string? StartedAt, string? EndedAt, double? DurationMs, IReadOnlyList<WorkspaceResultScore> Scores,
    WorkspaceResultTrials? Trials, WorkspaceResultComponent? Component, WorkspaceResultAggregation? Aggregation,
    WorkspaceResultAnnotator? Annotator, WorkspaceResultRule? VerdictRule, IReadOnlyList<WorkspaceResultUsage> Usage,
    IReadOnlyList<string> EvidenceIds, string RecordedJson)
{
    /// <summary>Validated captured legacy details on a root, when present; native records are never synthesized.</summary>
    public string? CapturedLegacyJson { get; init; }
}

/// <summary>A recorded numeric score; a normalized value exists only when explicitly recorded.</summary>
public sealed record WorkspaceResultScore(string MetricId, double Value, double? Normalized, string? Label);
/// <summary>Recorded repeated-trial summary, distinct from an individual trial index.</summary>
public sealed record WorkspaceResultTrials(long N, long Passed, string Aggregation, bool Agree, long? K);
/// <summary>Recorded contribution policy of a child.</summary>
public sealed record WorkspaceResultComponent(double Weight, bool Required);
/// <summary>Recorded composite policy and child census, without inferring decisive children.</summary>
public sealed record WorkspaceResultAggregation(string Strategy, string RulePath, double? Score, double? Threshold,
    long Measured, long Total, double? MinimumMeasuredShare, IReadOnlyList<string> Decisive,
    IReadOnlyList<WorkspaceAbsenceCount> Unmeasured);
/// <summary>Recorded absent-child count.</summary>
public sealed record WorkspaceAbsenceCount(string State, long Count);
/// <summary>Recorded grading instrument and panel census, if present.</summary>
public sealed record WorkspaceResultAnnotator(string Kind, string? Model, string? PromptHash, string? RubricDigest,
    long? PanelAgree, long? PanelOf);
/// <summary>Recorded verdict rule; no current registry threshold is substituted.</summary>
public sealed record WorkspaceResultRule(string Expr, double? Threshold, string? Source);
/// <summary>Recorded usage for one role/model. Missing cost and token counts remain unknown.</summary>
public sealed record WorkspaceResultUsage(string Role, string? Model, double? CostUsd, string? CostSource,
    long? InputTokens, long? OutputTokens, long? CacheReadInputTokens, long? CacheWriteInputTokens, long? ReasoningOutputTokens);
