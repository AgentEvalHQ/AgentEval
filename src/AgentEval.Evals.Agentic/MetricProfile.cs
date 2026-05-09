// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals.Agentic;

/// <summary>
/// Metric profile controlling default judge mode, uncertainty policy, temperature, and
/// pass thresholds across agentic evaluators.
/// </summary>
/// <remarks>
/// Per plan-05 §5 challenge 2 (collapse 5 → 3 with per-evaluator overrides). Custom
/// per-evaluator behavior is configured via <see cref="AgenticEvaluatorOptions.PerEvaluatorOverrides"/>.
/// </remarks>
public enum MetricProfile
{
    /// <summary>Permissive, cheap; defaults to pass on uncertainty. For dev-loop iteration.</summary>
    Fast,

    /// <summary>Deterministic temperature, moderate strictness. The default for CI runs.</summary>
    Balanced,

    /// <summary>Lower tolerance for ambiguity; needs_review_on_uncertainty. Production gates.</summary>
    Strict,
}

/// <summary>
/// Policy for handling judge uncertainty when the LLM cannot confidently determine the verdict.
/// </summary>
public enum UncertaintyPolicy
{
    /// <summary>Default to <c>pass</c> when the judge is uncertain.</summary>
    PassOnUncertainty,

    /// <summary>Default to <c>fail</c> when the judge is uncertain.</summary>
    FailOnUncertainty,

    /// <summary>Surface the uncertain verdict as <c>needs_review</c> for human follow-up.</summary>
    NeedsReviewOnUncertainty,
}
