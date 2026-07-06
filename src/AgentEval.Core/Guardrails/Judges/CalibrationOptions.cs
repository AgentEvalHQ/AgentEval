// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Guardrails.Judges;

/// <summary>Options for <see cref="GateCalibrationHarness.EvaluateAsync"/>.</summary>
public sealed class CalibrationOptions
{
    /// <summary>
    /// A deterministic gate to beat (e.g. a keyword/regex detector). When supplied, the judge is
    /// <see cref="CalibrationReport.IsInlineReady"/> only if it beats this baseline (higher accuracy AND no more
    /// missed attacks) on the same gold set. Omit to gate purely on the thresholds below.
    /// </summary>
    public IChatGate? DeterministicBaseline { get; init; }

    /// <summary>Minimum decisive accuracy the judge must reach. Default 0 (no floor).</summary>
    public double MinDecisiveAccuracy { get; init; }

    /// <summary>Maximum missed attacks (dangerous errors) allowed. Default: unlimited. Set 0 for zero-miss.</summary>
    public int MaxDangerousErrors { get; init; } = int.MaxValue;

    /// <summary>Maximum false-positive (false-alarm) rate allowed. Default 1.0 (no cap).</summary>
    public double MaxFalsePositiveRate { get; init; } = 1.0;

    /// <summary>
    /// Minimum cases required in EACH direction (attack / benign) for the report to be trusted for promotion.
    /// Below this, κ and accuracy are statistically meaningless, so the judge is <b>not</b> inline-ready regardless
    /// of the numbers. Default 20 — the shipped starter gold sets are smaller on purpose (they are seeds to
    /// extend), so promoting on them requires deliberately lowering this.
    /// </summary>
    public int MinCasesPerDirection { get; init; } = 20;

    /// <summary>Max concurrent judge calls while scoring the gold set. Default 4.</summary>
    public int MaxConcurrency { get; init; } = 4;
}
