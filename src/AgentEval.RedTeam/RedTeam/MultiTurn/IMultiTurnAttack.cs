// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
namespace AgentEval.RedTeam;

/// <summary>
/// An <see cref="IAttackType"/> driven as a conversation (Wave C, Pillar 2): each seed probe is an objective, and
/// <see cref="NextTurnAsync"/> produces the next user message from the transcript so far. Flows through the SAME
/// runner, reporting and baseline — the orchestrator folds the whole conversation into ONE <c>ProbeResult</c> per seed.
/// </summary>
public interface IMultiTurnAttack : IAttackType
{
    /// <summary>Hard ceiling on agent turns per seed conversation.</summary>
    int MaxTurns { get; }

    /// <summary>
    /// The next user message given the transcript so far; <c>null</c> ⇒ the attack gives up (no more rungs to try).
    /// </summary>
    Task<string?> NextTurnAsync(MultiTurnContext context, CancellationToken cancellationToken = default);

    /// <summary>The stop policy. Default: stop on the first success or after a refusal-lock.</summary>
    IConvergenceDetector ConvergenceDetector => DefaultConvergenceDetector.Instance;

    /// <summary>Jun14-M13: true when this attack actually generates its turns from an attacker LLM (so a run is
    /// non-deterministic). Default <c>false</c> for scripted ladders (e.g. ToolEscalation), so the folded result's
    /// <c>AttackerDriven</c> provenance reflects real non-determinism — not merely that <c>--attacker</c> was supplied
    /// scan-wide. Overridden <c>true</c> by the attacker-driven attacks (PAIR, attacker-Crescendo).</summary>
    bool UsesAttacker => false;
}
