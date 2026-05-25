// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.Cost; // T3.1: EvaluatorCostMap doc-cref binding.

namespace AgentEval.MissionControl.GraphQL;

/// <summary>
/// Per-cost-tier breakdown of a run's estimated cost. Drives the
/// <c>&lt;CostTierBreakdownChart/&gt;</c> stacked bar in plan-07 §10.
/// </summary>
/// <param name="TotalCost">Sum of <see cref="EvalProvenance.EstimatedCost"/> across every leaf in the recursive <c>EvalResult</c> tree of every scenario.</param>
/// <param name="ByTier">Per-cost-tier sub-totals. The four tiers always appear; tiers with zero cost have value 0.</param>
/// <param name="UnknownKeyCost">Sum of leaf costs whose evaluator key did not resolve in <see cref="EvaluatorCostMap"/> — informational, surfaced separately so users notice unregistered evaluators ("modern entries without category mapping").</param>
/// <param name="LegacyFlatCost">Sum of scenario costs from PRE-v0.8.1-beta evidence that lacks a recursive <c>EvalResult</c> tree (the Output field is a flat agent response rather than a structured composite). These scenarios cannot be attributed per-leaf because the tier-aware path requires the tree shape; surfaced separately so operators can see how much of the total is "blind" cost from the legacy serializer. T3.5.</param>
/// <param name="FilteredOut">List of evaluator keys that were excluded by <c>--budget-tier</c> at run time. Empty until <c>RunRef.BudgetTier</c> ships in a follow-up — see plan-08 backlog.</param>
public sealed record RunCostBreakdown(
    double TotalCost,
    CostByTier ByTier,
    double UnknownKeyCost,
    double LegacyFlatCost,
    IReadOnlyList<string> FilteredOut);

/// <summary>Tier-keyed cost sub-totals.</summary>
public sealed record CostByTier(
    double Trivial,
    double Low,
    double Medium,
    double High);
