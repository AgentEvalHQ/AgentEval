// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Guardrails.Judges;

/// <summary>
/// Marks a gate whose right to <b>block live traffic inline</b> is contingent on calibration — an LLM judge that
/// must have beaten its deterministic baseline on a gold set (<see cref="CalibrationReport.IsInlineReady"/>)
/// before it may enforce. A construction-time guard (Fable 5 §9 / P1-1) can enumerate these and refuse to promote
/// an un-proven judge inline, turning the repo's calibration-honesty discipline from documentation into an
/// enforced invariant — the same fail-loud posture as <c>RefuseUnprotectedHighRiskTools</c> /
/// <c>PromptTemplateBaseline</c>.
/// </summary>
public interface IRequiresCalibration
{
    /// <summary>The calibration axis id (matches <see cref="CalibrationReport.Axis"/> in the report store) whose
    /// <see cref="CalibrationReport.IsInlineReady"/> flag decides whether this gate may block inline.</summary>
    string AxisName { get; }
}
