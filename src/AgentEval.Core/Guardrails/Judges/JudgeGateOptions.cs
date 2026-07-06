// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Guardrails.Judges;

/// <summary>Options for a <see cref="CompositeJudgeGate{TRubric}"/>.</summary>
public sealed class JudgeGateOptions
{
    /// <summary>
    /// Max wall-clock for one judge call. On timeout the verdict is <see cref="JudgeDecision.Inconclusive"/>
    /// (never stalls the hot path). Default 5s.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Minimum <see cref="JudgeVerdict.Confidence"/> to act on a <see cref="JudgeDecision.Blocked"/> verdict.
    /// <b>Default 0 — fail-closed: any block blocks.</b> Raising it above 0 introduces a deliberate <i>fail-open
    /// band</i>: a <see cref="JudgeDecision.Blocked"/> verdict below the threshold is downgraded to allow (trading
    /// safety for fewer false alarms). Only raise it if you have calibrated the cost of that band.
    /// </summary>
    public double BlockThreshold { get; init; }

    /// <summary>Cap on the judge's output tokens (a judge answers briefly). Default 256.</summary>
    public int MaxOutputTokens { get; init; } = 256;

    /// <summary>
    /// When the judge is <see cref="JudgeDecision.Inconclusive"/> (timeout / model error / unparseable reply),
    /// whether to block (fail-closed) or allow (fail-open, observe-only). Default <c>true</c> (fail-closed) — a
    /// gate that cannot prove a turn safe should not pass it under a blocking policy. Enforcement is still gated
    /// by the run gate's <see cref="EvalGatePolicy"/> (a block only stops the run under a blocking policy).
    /// </summary>
    public bool FailClosedOnInconclusive { get; init; } = true;
}
