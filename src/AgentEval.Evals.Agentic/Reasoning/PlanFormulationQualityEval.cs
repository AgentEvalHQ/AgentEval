// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;

namespace AgentEval.Evals.Agentic.Reasoning;

/// <summary>
/// Evaluates whether an AI agent produced a sound, well-structured plan before executing a task.
/// <para>
/// The plan is sourced from <see cref="EvalInput.Metadata"/> key <c>"plan"</c> (string) when
/// present. If that key is absent, the evaluator scans <see cref="EvalInput.Response"/> for
/// plan-like structural patterns: numbered or bulleted lists, or sequential-connective prefixes
/// such as "First, ... Then, ... Finally, ...". When neither source yields a recognisable plan,
/// the evaluator returns <see cref="EvalResult.Skipped"/> — there is nothing to assess.
/// </para>
/// <para>
/// Assessment criteria:
/// <list type="bullet">
///   <item>The plan is well-structured and internally consistent.</item>
///   <item>The plan directly addresses the stated goal.</item>
///   <item>The plan decomposes the goal into concrete, appropriately-scoped steps.</item>
///   <item>The plan acknowledges or accounts for preconditions and inter-step dependencies.</item>
/// </list>
/// </para>
/// <para>
/// <b>Optional metadata keys consumed:</b>
/// <list type="bullet">
///   <item><c>"plan"</c> (string) — the agent's explicit plan text. Takes precedence over
///   scanning <see cref="EvalInput.Response"/>.</item>
/// </list>
/// </para>
/// <para>
/// <b>Cost tier: MEDIUM</b> — single LLM judge call over a plan + query context.
/// Estimated ~$0.01-0.05 per scenario (GPT-4-class judge).
/// </para>
/// <para>
/// Source: AgentEval-original. Plan 06 §3.3 B3.1.
/// </para>
/// </summary>
public sealed class PlanFormulationQualityEval : IEval
{
    /// <summary>
    /// Metadata key holding the agent's explicit plan text. When present and non-empty,
    /// it takes precedence over scanning <see cref="EvalInput.Response"/>.
    /// </summary>
    public const string MetadataKey = "plan";

    // Plan-like structural markers we look for in the response when no explicit plan is provided.
    private static readonly string[] PlanPrefixes =
    [
        "1.", "1)", "step 1", "first,", "firstly,",
        "- ", "* ", "• ",
    ];

    private readonly AtomicLlmEval _inner;

    /// <inheritdoc/>
    public string Key => _inner.Key;

    /// <inheritdoc/>
    public string Name => _inner.Name;

    /// <inheritdoc/>
    public string Category => _inner.Category;

    /// <inheritdoc/>
    public string Version => _inner.Version;

    /// <summary>
    /// Initialises a new <see cref="PlanFormulationQualityEval"/>.
    /// </summary>
    /// <param name="judge">The LLM evaluator used to score plan quality.</param>
    /// <param name="judgeModel">Optional judge model identifier recorded in provenance.</param>
    /// <param name="passThreshold">
    /// Score fraction (0..1) at or above which the eval passes. Defaults to <c>0.75</c>.
    /// </param>
    public PlanFormulationQualityEval(IEvaluator judge, string? judgeModel = null, double passThreshold = 0.75)
    {
        ArgumentNullException.ThrowIfNull(judge);
        _inner = new AtomicLlmEval(
            evaluator: judge,
            key: "plan_formulation_quality",
            name: "Plan Formulation Quality",
            category: "reasoning",
            version: "1.0.0",
            criteria: new[]
            {
                "Plan is well-structured and internally consistent",
                "Plan directly addresses the stated goal",
                "Plan decomposes the goal into concrete, appropriately-scoped steps",
                "Plan acknowledges or accounts for preconditions and inter-step dependencies",
            },
            passThreshold: passThreshold,
            judgeModel: judgeModel,
            promptId: "agenteval.plan_formulation_quality.v1",
            failureSeverity: "medium");
    }

    /// <inheritdoc/>
    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Resolve the plan text: prefer explicit metadata key, fall back to response scan.
        var planText = ResolvePlan(input);
        if (planText is null)
            return Task.FromResult(EvalResult.Skipped(this,
                $"No plan found: '{MetadataKey}' key absent from Metadata and no plan-like structure detected in Response."));

        // If the plan is separate from the response, inject it as context for the judge by
        // constructing a synthetic input where the plan text is prepended to the query context.
        var judgeInput = planText != input.Response
            ? input with { Context = $"[Agent plan]\n{planText}\n\n{input.Context}".TrimEnd() }
            : input;

        return _inner.EvaluateAsync(judgeInput, ct);
    }

    /// <summary>
    /// Returns the plan text to evaluate, or <c>null</c> if no plan can be located.
    /// </summary>
    private static string? ResolvePlan(EvalInput input)
    {
        // Priority 1: explicit metadata key.
        if (input.Metadata is not null &&
            input.Metadata.TryGetValue(MetadataKey, out var planObj) &&
            planObj is string planStr &&
            !string.IsNullOrWhiteSpace(planStr))
        {
            return planStr;
        }

        // Priority 2: look for plan-like structure in the response.
        if (input.Response is not null)
        {
            foreach (var prefix in PlanPrefixes)
            {
                if (input.Response.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                    return input.Response;
            }
        }

        return null;
    }
}
