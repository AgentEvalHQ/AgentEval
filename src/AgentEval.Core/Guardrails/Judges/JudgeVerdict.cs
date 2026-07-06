// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Guardrails.Judges;

/// <summary>What a single-axis judge rubric decided about one turn.</summary>
public enum JudgeDecision
{
    /// <summary>The axis was not present — allow.</summary>
    Allowed,

    /// <summary>The axis was present — block.</summary>
    Blocked,

    /// <summary>The judge could not decide (timeout, model error, unparseable reply) — an abstention.</summary>
    Inconclusive,
}

/// <summary>
/// A single-axis judge rubric's decision, with a confidence and the cited evidence spans. Deliberately decisive
/// (<see cref="JudgeDecision"/>) rather than a bare score — a runtime gate must act, and an abstention
/// (<see cref="JudgeDecision.Inconclusive"/>) is a first-class outcome (a timeout / model error / unparseable
/// reply), handled fail-closed by the gate under a blocking policy.
/// </summary>
/// <param name="Decision">The decisive outcome.</param>
/// <param name="Confidence">0..1 confidence in the decision (used against the gate's block threshold).</param>
/// <param name="Spans">The offending text spans cited as evidence (recorded into the gate verdict's matches).</param>
/// <param name="Rationale">A short human-readable reason.</param>
public sealed record JudgeVerdict(
    JudgeDecision Decision,
    double Confidence = 1.0,
    IReadOnlyList<string>? Spans = null,
    string? Rationale = null)
{
    /// <summary>The axis was not present.</summary>
    public static JudgeVerdict Allowed(double confidence = 1.0) => new(JudgeDecision.Allowed, confidence);

    /// <summary>The axis was present — block, citing the evidence spans.</summary>
    public static JudgeVerdict Blocked(string? rationale = null, IReadOnlyList<string>? spans = null, double confidence = 1.0)
        => new(JudgeDecision.Blocked, confidence, spans, rationale);

    /// <summary>The judge could not decide — an abstention (handled fail-closed by the gate under a blocking policy).</summary>
    public static JudgeVerdict Inconclusive(string? rationale = null)
        => new(JudgeDecision.Inconclusive, 0.0, null, rationale);
}
