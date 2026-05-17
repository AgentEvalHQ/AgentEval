// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;

namespace AgentEval.EuAiActBenchmark;

/// <summary>
/// Options for multi-judge evaluation of Critical articles in the Audit-Grade preset.
/// </summary>
/// <param name="Judges">
/// Ordered list of judges (evaluator + weight) used for consensus aggregation
/// over Critical-severity articles. Typically 3 judges with equal weights.
/// </param>
/// <remarks>
/// Phase-8 Task 8.5: this record is <see cref="ObsoleteAttribute"/>-marked
/// because Mode-B per-criterion multi-judge fan-out has moved into
/// <c>ScenarioToAtomicEval</c> ctor flags. See <c>deferred-pending.md</c>.
/// </remarks>
[Obsolete("MultiJudgeOptions is deprecated; configure multi-judge via ScenarioToAtomicEval Mode-B flags instead. " +
          "Pass `null` to AuditGrade to retain single-judge behaviour. Removal scheduled for v1.1.")]
public sealed record MultiJudgeOptions(
    IReadOnlyList<(IEvaluator Judge, double Weight)> Judges);
