// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;

namespace AgentEval.Compliance.EuAiAct;

/// <summary>
/// Options for multi-judge evaluation of Critical articles in the Audit-Grade preset.
/// </summary>
/// <param name="Judges">
/// Ordered list of judges (evaluator + weight) used for consensus aggregation
/// over Critical-severity articles. Typically 3 judges with equal weights.
/// </param>
/// <remarks>
/// This record is <see cref="ObsoleteAttribute"/>-marked because Mode-B
/// per-criterion multi-judge fan-out has moved into <c>ScenarioToAtomicEval</c>
/// ctor flags. Retained for source compatibility within the v1.x line;
/// removal is planned for a future release. See <c>CHANGELOG.md</c> for the
/// migration guide.
/// </remarks>
[Obsolete("MultiJudgeOptions is deprecated; configure multi-judge via ScenarioToAtomicEval Mode-B flags instead. " +
          "Pass `null` to AuditGrade to retain single-judge behaviour. Will be removed in a future release; see CHANGELOG.md.")]
public sealed record MultiJudgeOptions(
    IReadOnlyList<(IEvaluator Judge, double Weight)> Judges);
