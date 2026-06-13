// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Testing;    // Turn

namespace AgentEval.RedTeam;

/// <summary>
/// The folded outcome of one multi-turn conversation for a seed (Wave C). The runner collapses this into a single
/// <c>ProbeResult</c>; <see cref="PerTurnResults"/> is the per-turn verdict stream the orchestrator produced.
/// </summary>
public sealed class MultiTurnResult
{
    /// <summary>
    /// Folded verdict over the conversation:
    /// <list type="bullet">
    ///   <item><b>Succeeded</b> — any turn succeeded (the orchestrator stops at the first success).</item>
    ///   <item><b>Resisted</b> — a refusal-lock fired, or the ladder/turns were exhausted with at least one
    ///         conclusive turn and no success.</item>
    ///   <item><b>Inconclusive</b> — no turn ran, OR every executed turn was itself Inconclusive and nothing
    ///         actively signalled resistance (a refusal-lock). The fold never fabricates Resisted from a
    ///         conversation that measured nothing conclusive (honesty discipline).</item>
    /// </list>
    /// </summary>
    public required EvaluationOutcome Outcome { get; init; }

    /// <summary>Fidelity of the evidence behind <see cref="Outcome"/>: the succeeding turn's fidelity on success;
    /// otherwise the highest fidelity among CONCLUSIVE turns (Inconclusive turns are not evidence behind the fold).</summary>
    public required EvidenceFidelity Fidelity { get; init; }

    /// <summary>How faithfully the conversation channel carried history (Native vs Flattened).</summary>
    public required ConversationFidelity ConversationFidelity { get; init; }

    /// <summary>The full transcript (user/assistant turns) in order.</summary>
    public required IReadOnlyList<Turn> Transcript { get; init; }

    /// <summary>The per-turn verdicts, in order — the verdict stream.</summary>
    public required IReadOnlyList<EvaluationResult> PerTurnResults { get; init; }

    /// <summary>Number of agent turns actually executed.</summary>
    public int TurnsUsed { get; init; }

    /// <summary>Why the conversation stopped (success / refusal-lock / exhausted rungs / max turns / duration).</summary>
    public string Reason { get; init; } = "";

    /// <summary>True if the conversation hit a turn/duration cap without converging (not a clean win or refusal-lock).</summary>
    public bool WasTruncated { get; init; }
}
