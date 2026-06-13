// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
namespace AgentEval.RedTeam;

/// <summary>
/// An attacker-LLM attack driven as a <b>tree search</b> rather than a linear conversation (Wave C′, TAP — Mehrotra et
/// al. 2023): at each node the attacker proposes several candidate prompts; each is sent to the target and scored;
/// the best are kept (beam) and expanded. Flows through the SAME runner/reporting/baseline — the
/// <c>TreeOrchestrator</c> folds the whole search into ONE <c>ProbeResult</c> per seed. Requires
/// <see cref="ScanOptions.AttackerClient"/>.
/// </summary>
public interface ITreeAttack : IAttackType
{
    /// <summary>Maximum tree depth (refinement rounds).</summary>
    int MaxDepth { get; }

    /// <summary>Candidate prompts the attacker proposes per node (branching factor).</summary>
    int Branching { get; }

    /// <summary>Survivors kept after pruning each level (beam width).</summary>
    int Beam { get; }

    /// <summary>Hard cap on total target calls across the whole search (budget; exceeding it is an honest truncation).</summary>
    int MaxNodes { get; }

    /// <summary>The natural-language objective the attacker pursues for a given seed.</summary>
    string Objective(AttackProbe seed);
}
