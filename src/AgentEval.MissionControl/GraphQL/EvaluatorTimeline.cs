// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Output;

namespace AgentEval.MissionControl.GraphQL;

/// <summary>
/// One data point on a per-evaluator timeline — a single occurrence of the
/// evaluator's <see cref="AgentEval.Evals.EvalResult"/> within a recent run.
/// Drives <c>&lt;EvaluatorTimelineChart/&gt;</c> on the evaluator-detail page
/// (plan-08 Wave 8b / MC1.6.9). For the <c>judge_drift</c>, <c>judge_agreement</c>,
/// and <c>calibration_accuracy</c> cards in particular, the chart over time
/// is the calibration / drift surface called out in plan-07 §6.1.
/// </summary>
/// <param name="RunId">The run that produced this datapoint.</param>
/// <param name="Timestamp">The run's wall-clock timestamp (sortable; SPA charts on this).</param>
/// <param name="SubjectKind">Kind of the subject (Agent / Workflow) — useful when the same evaluator runs against multiple subjects.</param>
/// <param name="SubjectName">Subject name for tooltip / colouring.</param>
/// <param name="Score">The evaluator's score for this run.</param>
/// <param name="Passed">Whether the evaluator passed for this run.</param>
/// <param name="Severity">Severity carried on the evaluator's score (none/low/medium/high/critical).</param>
/// <param name="Confidence">Optional self-reported confidence (judge models often emit this).</param>
/// <param name="ManifestHash">Source run's manifest content hash — supports audit-chain verification client-side.</param>
public sealed record EvaluatorTimelinePoint(
    string RunId,
    DateTimeOffset Timestamp,
    SubjectKind SubjectKind,
    string SubjectName,
    double Score,
    bool Passed,
    string Severity,
    double? Confidence,
    string ManifestHash);
