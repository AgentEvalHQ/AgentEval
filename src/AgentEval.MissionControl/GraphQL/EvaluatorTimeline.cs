// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Writing;

namespace AgentEval.MissionControl.GraphQL;

/// <summary>One recorded canonical occurrence. Unknown execution time, measurement and decision remain null.</summary>
public sealed record EvaluatorTimelinePoint(
    string RunId,
    DateTimeOffset? Timestamp,
    AefSubjectKind SubjectKind,
    string SubjectName,
    double? Score,
    bool? Passed,
    string? Severity,
    double? Confidence,
    string? ManifestHash)
{
    /// <summary>Verified source identity; a run ID alone is not unique.</summary>
    public required string SourceId { get; init; }
    /// <summary>Canonical result identity.</summary>
    public required string ResultId { get; init; }
    /// <summary>Recorded case identity.</summary>
    public required string CaseId { get; init; }
    /// <summary>Recorded result path.</summary>
    public required string Path { get; init; }
    /// <summary>Recorded trial index; null for a final result.</summary>
    public int? Trial { get; init; }
    /// <summary>Recorded evaluator version, without substituting the current registry.</summary>
    public string? EvaluatorVersion { get; init; }
    /// <summary>Exact subject reference.</summary>
    public required string SubjectRef { get; init; }
    /// <summary>Recorded outcome, including unmeasured and unfamiliar states.</summary>
    public required string State { get; init; }
    /// <summary>The unambiguous normalized score metric, when one is declared.</summary>
    public string? ScoreMetricId { get; init; }
    /// <summary>Recorded decision threshold.</summary>
    public double? Threshold { get; init; }
    /// <summary>All recorded score values.</summary>
    public required IReadOnlyList<WorkspaceResultScore> Scores { get; init; }
    /// <summary>Declared meanings of the recorded scores.</summary>
    public required IReadOnlyList<WorkspaceMetricDefinition> Metrics { get; init; }
    /// <summary>Container integrity and custody, separate from execution observations.</summary>
    public required AefRunReadInfo Evidence { get; init; }
    /// <summary>Original canonical result JSON, retaining unknown fields.</summary>
    public required string RecordedJson { get; init; }
}
