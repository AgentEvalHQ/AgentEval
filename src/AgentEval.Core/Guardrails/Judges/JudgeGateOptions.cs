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

    /// <summary>
    /// Cap on the judge's output tokens. Default 1024: reasoning-capable models may consume part of this budget
    /// internally before emitting the short classifier JSON, so the former 256-token cap could produce an empty,
    /// length-truncated response that the fail-closed gate correctly—but misleadingly—treated as a block.
    /// </summary>
    public int MaxOutputTokens { get; init; } = 1024;

    /// <summary>
    /// Optional sampling temperature for the judge call. The default is <see langword="null"/>, which lets the
    /// provider use its supported default. Set this explicitly only when the selected model supports the value,
    /// and calibrate that exact configuration. Some reasoning-model deployments reject an explicit
    /// <c>temperature=0</c>; leaving it unspecified avoids turning a provider capability mismatch into a
    /// fail-closed block on every inspected case.
    /// </summary>
    public float? Temperature { get; init; }

    /// <summary>
    /// Cap on the number of input characters the judge sends to the model (Phase 5, P5-3 — bounds cost, latency,
    /// and context-overflow risk on a pathologically large turn). Over the cap, the text is reduced to a
    /// head + tail sandwich with a truncation marker so an injection payload at <i>either</i> boundary is still
    /// seen. The <see cref="IJudgeRubric.Prefilter"/> always runs on the FULL text — only the model prompt is
    /// bounded. Default 16000 (≈4k tokens). <c>0</c> means unbounded (the pre-P5-3 behavior); any other value must
    /// be at least 256 (a floor, so a fat-fingered tiny cap can't collapse the head+tail sandwich and blind the
    /// judge) — the gate constructor throws otherwise.
    /// </summary>
    public int MaxInputChars { get; init; } = 16_000;

    /// <summary>
    /// When the judge is <see cref="JudgeDecision.Inconclusive"/> (timeout / model error / unparseable reply),
    /// whether to block (fail-closed) or allow (fail-open, observe-only). Default <c>true</c> (fail-closed) — a
    /// gate that cannot prove a turn safe should not pass it under a blocking policy. Enforcement is still gated
    /// by the run gate's <see cref="EvalGatePolicy"/> (a block only stops the run under a blocking policy).
    /// </summary>
    public bool FailClosedOnInconclusive { get; init; } = true;

    /// <summary>
    /// Optional shared token + call budget across judges (Phase 5, P5-2 — a denial-of-wallet / runaway-cost bound).
    /// When set, the gate reserves against it before calling the model; if the budget for the current window is
    /// exhausted the model call is SKIPPED and the turn degrades per <see cref="FailClosedOnBudgetExhausted"/>.
    /// Share one instance across every <see cref="CompositeJudgeGate{TRubric}"/> that draws on the same wallet.
    /// Default <c>null</c> (no budget cap — the pre-P5-2 behavior).
    /// </summary>
    public JudgeSpendGovernor? SpendGovernor { get; init; }

    /// <summary>
    /// When <see cref="SpendGovernor"/> refuses a reservation (budget exhausted), whether the un-run judge blocks
    /// (fail-closed) or allows (fail-open, recorded as "unjudged — budget exhausted"). Default <c>false</c>
    /// (fail-open): a spent <i>cost</i> budget must not turn into a full traffic outage — exhaustion is a resource
    /// limit, not a detection failure, so the judge degrades to advisory rather than blocking everything. Set
    /// <c>true</c> only where an unjudged turn is unacceptable and a judge outage blocking all traffic is preferred.
    /// <para><b>Attack note:</b> with the default (fail-open) and a <i>shared</i> <see cref="SpendGovernor"/>, an
    /// adversary who first floods the window's budget with junk turns can then push a payload turn past this judge
    /// un-inspected. This judge is one layer — deterministic gates still run — but if the semantic judge is
    /// load-bearing for your threat model, either give it an isolated (not shared) governor, size the budget above
    /// plausible adversarial volume, or set this <c>true</c> and accept the self-DoS tradeoff.</para>
    /// </summary>
    public bool FailClosedOnBudgetExhausted { get; init; }
}
