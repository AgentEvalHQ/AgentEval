// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals.Agentic;

/// <summary>
/// DI options for the agentic evaluator suite. Per plan-05 §5 challenge 2: 3 profiles
/// (<see cref="MetricProfile"/>) plus per-evaluator overrides cover every documented
/// use-case more flexibly than 5 fixed profiles.
/// </summary>
public sealed class AgenticEvaluatorOptions
{
    /// <summary>The default profile applied to every evaluator unless overridden.</summary>
    public MetricProfile DefaultProfile { get; set; } = MetricProfile.Balanced;

    /// <summary>
    /// Per-evaluator overrides keyed by evaluator key (e.g. <c>"task_completion"</c>,
    /// <c>"prohibited_actions"</c>).
    /// </summary>
    public Dictionary<string, EvaluatorOverride> PerEvaluatorOverrides { get; set; } = new();
}

/// <summary>
/// Per-evaluator override of profile, judge mode, and uncertainty policy.
/// </summary>
/// <param name="Profile">Override profile; <c>null</c> uses the default.</param>
/// <param name="UncertaintyPolicy">Override uncertainty policy; <c>null</c> derives from <paramref name="Profile"/>.</param>
/// <param name="PassThreshold">Override pass threshold (0..1); <c>null</c> uses the evaluator's per-prompt default.</param>
public sealed record EvaluatorOverride(
    MetricProfile? Profile = null,
    UncertaintyPolicy? UncertaintyPolicy = null,
    double? PassThreshold = null);
