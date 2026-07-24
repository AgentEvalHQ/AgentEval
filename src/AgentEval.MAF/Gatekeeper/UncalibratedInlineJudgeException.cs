// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Thrown by <see cref="GatekeeperOptions.ValidateInlineJudgesAsync"/> when an inline LLM judge has not earned the
/// right to block — no calibration report exists for its axis, or the latest report is not inline-ready (Fable 5
/// §9 / P1-1). Mirrors the fail-loud posture of <c>PromptTemplateDriftException</c> / <c>SkillDriftException</c>:
/// an un-proven judge that hard-blocks is a fabrication risk the toolkit argues is worse than no gate at all.
/// </summary>
public sealed class UncalibratedInlineJudgeException : Exception
{
    /// <summary>The calibration axis of the judge that failed the readiness check.</summary>
    public string AxisName { get; }

    /// <summary>Creates the exception for <paramref name="axisName"/> with the given <paramref name="reason"/>.</summary>
    public UncalibratedInlineJudgeException(string axisName, string reason)
        : base($"inline judge '{axisName}' may not block: {reason}. Calibrate it (GateCalibrationHarness) and persist "
               + "an inline-ready report to the CalibrationReportStore, or set GatekeeperOptions.AllowUncalibratedInlineJudge to override.")
    {
        AxisName = axisName;
    }
}
