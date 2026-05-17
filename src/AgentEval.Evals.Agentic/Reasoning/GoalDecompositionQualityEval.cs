// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;

namespace AgentEval.Evals.Agentic.Reasoning;

/// <summary>
/// Evaluates whether an AI agent broke down a complex goal into an appropriate set of sub-goals.
/// <para>
/// The decomposition is sourced from <see cref="EvalInput.Metadata"/> key <c>"plan"</c> (string)
/// when present — the plan is the most reliable source of explicit goal decomposition. If absent,
/// the evaluator falls back to <see cref="EvalInput.Response"/>, looking for any evidence of
/// enumerated sub-goals or sub-tasks. When neither source contains a recognisable decomposition,
/// the evaluator returns <see cref="EvalResult.Skipped"/>.
/// </para>
/// <para>
/// Assessment criteria:
/// <list type="bullet">
///   <item>The agent identified the correct high-level sub-goals for the stated task.</item>
///   <item>The decomposition is at an appropriate granularity (not too coarse, not over-fragmented).</item>
///   <item>Sub-goals are mutually consistent and collectively sufficient to achieve the main goal.</item>
///   <item>Dependencies between sub-goals are correctly ordered or acknowledged.</item>
/// </list>
/// </para>
/// <para>
/// <b>Optional metadata keys consumed:</b>
/// <list type="bullet">
///   <item><c>"plan"</c> (string) — the agent's explicit plan, which is the primary decomposition
///   source. Takes precedence over scanning <see cref="EvalInput.Response"/>.</item>
/// </list>
/// </para>
/// <para>
/// <b>Cost tier: LOW</b> — single-turn LLM judgment of the decomposition.
/// Estimated ~$0.005-0.01 per scenario (GPT-4-class judge).
/// </para>
/// <para>
/// Source: AgentEval-original. Plan 06 §3.3 B3.2.
/// </para>
/// </summary>
public sealed class GoalDecompositionQualityEval : IEval
{
    // Structural markers indicating some form of goal breakdown is present.
    private static readonly string[] DecompositionMarkers =
    [
        "1.", "1)", "step 1", "first,", "firstly,",
        "sub-goal", "subtask", "sub-task", "phase ",
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
    /// Initialises a new <see cref="GoalDecompositionQualityEval"/>.
    /// </summary>
    /// <param name="judge">The LLM evaluator used to score goal decomposition quality.</param>
    /// <param name="judgeModel">Optional judge model identifier recorded in provenance.</param>
    /// <param name="passThreshold">
    /// Score fraction (0..1) at or above which the eval passes. Defaults to <c>0.75</c>.
    /// </param>
    public GoalDecompositionQualityEval(IEvaluator judge, string? judgeModel = null, double passThreshold = 0.75)
    {
        ArgumentNullException.ThrowIfNull(judge);
        _inner = new AtomicLlmEval(
            evaluator: judge,
            key: "goal_decomposition_quality",
            name: "Goal Decomposition Quality",
            category: "reasoning",
            version: "1.0.0",
            criteria: new[]
            {
                "The agent identified the correct high-level sub-goals for the stated task",
                "The decomposition is at an appropriate granularity (not too coarse, not over-fragmented)",
                "Sub-goals are mutually consistent and collectively sufficient to achieve the main goal",
                "Dependencies between sub-goals are correctly ordered or acknowledged",
            },
            passThreshold: passThreshold,
            judgeModel: judgeModel,
            promptId: "agenteval.goal_decomposition_quality.v1",
            failureSeverity: "low");
    }

    /// <inheritdoc/>
    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Resolve decomposition source: prefer explicit plan metadata, fall back to response scan.
        var decompositionText = ResolveDecomposition(input);
        if (decompositionText is null)
            return Task.FromResult(EvalResult.Skipped(this,
                "No goal decomposition found: 'plan' key absent from Metadata and no sub-goal structure detected in Response."));

        // If the decomposition is sourced from the plan metadata rather than the response,
        // inject it into the context so the judge can see it alongside the query.
        var judgeInput = decompositionText != input.Response
            ? input with { Context = $"[Agent decomposition]\n{decompositionText}\n\n{input.Context}".TrimEnd() }
            : input;

        return _inner.EvaluateAsync(judgeInput, ct);
    }

    /// <summary>
    /// Returns the text to evaluate for decomposition quality, or <c>null</c> if none found.
    /// </summary>
    private static string? ResolveDecomposition(EvalInput input)
    {
        // Priority 1: explicit plan metadata key (most reliable decomposition source).
        if (input.Metadata is not null &&
            input.Metadata.TryGetValue("plan", out var planObj) &&
            planObj is string planStr &&
            !string.IsNullOrWhiteSpace(planStr))
        {
            return planStr;
        }

        // Priority 2: scan response for decomposition markers.
        if (input.Response is not null)
        {
            var lower = input.Response.ToLowerInvariant();
            foreach (var marker in DecompositionMarkers)
            {
                if (lower.Contains(marker))
                    return input.Response;
            }
        }

        return null;
    }
}
