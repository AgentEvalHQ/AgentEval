// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.RedTeam;

/// <summary>A materialized attack arm, after transformation and configured probe limits.</summary>
/// <param name="Attack">The configured attack that executes and grades these probes.</param>
/// <param name="Probes">The ordered probes the runner will execute; generators are not called again.</param>
/// <param name="Control">Whether this is the benign-control arm.</param>
public sealed record PlannedAttack(IAttackType Attack, IReadOnlyList<AttackProbe> Probes, bool Control = false);

/// <summary>Pre-execution scan declaration, delivered before any subject or judge call.</summary>
/// <remarks>Inspect only: callbacks must not mutate referenced attacks, options or probe metadata.</remarks>
/// <param name="Options">The applied scan policy.</param>
/// <param name="Attacks">Ordered attack arms, followed by the enabled benign-control arm.</param>
public sealed record ScanPlan(ScanOptions Options, IReadOnlyList<PlannedAttack> Attacks);
