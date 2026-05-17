// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;

namespace AgentEval.Evals.Agentic.Reasoning;

/// <summary>
/// Evaluates whether an AI agent's reasoning process was sound — independently of whether the
/// final answer happens to be correct.
/// <para>
/// The reasoning trace is sourced from <see cref="EvalInput.Metadata"/> key
/// <c>"reasoning_trace"</c> (string) when present. If absent, the evaluator scans
/// <see cref="EvalInput.Response"/> for reasoning-style indicators such as chain-of-thought
/// prefixes ("Let me think...", "First, I need to consider...", "Therefore..."). When neither
/// source yields visible reasoning, the evaluator returns <see cref="EvalResult.Skipped"/> —
/// there is no observable reasoning process to assess.
/// </para>
/// <para>
/// This evaluator is <b>distinct from <c>TaskCompletionEval</c></b>: an agent can produce a
/// correct final answer via broken or circular reasoning (lucky coincidence, post-hoc
/// rationalisation). This evaluator specifically flags that failure mode by judging the
/// reasoning chain itself.
/// </para>
/// <para>
/// Assessment criteria:
/// <list type="bullet">
///   <item>Each reasoning step follows logically from the prior step or from stated premises.</item>
///   <item>Intermediate conclusions are consistent with each other and with the final answer.</item>
///   <item>The agent does not introduce unjustified assumptions in the reasoning chain.</item>
///   <item>The agent correctly applies relevant domain rules, constraints, or formulae cited in the reasoning.</item>
/// </list>
/// </para>
/// <para>
/// <b>Optional metadata keys consumed:</b>
/// <list type="bullet">
///   <item><c>"reasoning_trace"</c> (string) — the agent's explicit chain-of-thought or step-by-step
///   reasoning. Takes precedence over scanning <see cref="EvalInput.Response"/>.</item>
/// </list>
/// </para>
/// <para>
/// <b>Cost tier: MEDIUM</b> — LLM judge over a reasoning trace and query context.
/// Estimated ~$0.01-0.05 per scenario (GPT-4-class judge).
/// </para>
/// <para>
/// Source: AgentEval-original. Plan 06 §3.3 B3.3.
/// </para>
/// </summary>
public sealed class ReasoningCorrectnessEval : IEval
{
    /// <summary>
    /// Metadata key holding the agent's explicit reasoning trace. When present and non-empty,
    /// it takes precedence over scanning <see cref="EvalInput.Response"/>.
    /// </summary>
    public const string MetadataKey = "reasoning_trace";

    // Textual markers that suggest chain-of-thought or step-by-step reasoning in the response.
    private static readonly string[] ReasoningPrefixes =
    [
        "let me think", "first, i", "first i need", "step 1",
        "therefore,", "thus,", "hence,", "it follows that",
        "let's reason", "reasoning:", "analysis:", "because ",
        "since ", "given that ", "consequently,",
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
    /// Initialises a new <see cref="ReasoningCorrectnessEval"/>.
    /// </summary>
    /// <param name="judge">The LLM evaluator used to score reasoning soundness.</param>
    /// <param name="judgeModel">Optional judge model identifier recorded in provenance.</param>
    /// <param name="passThreshold">
    /// Score fraction (0..1) at or above which the eval passes. Defaults to <c>0.80</c>.
    /// </param>
    public ReasoningCorrectnessEval(IEvaluator judge, string? judgeModel = null, double passThreshold = 0.80)
    {
        ArgumentNullException.ThrowIfNull(judge);
        _inner = new AtomicLlmEval(
            evaluator: judge,
            key: "reasoning_correctness",
            name: "Reasoning Correctness",
            category: "reasoning",
            version: "1.0.0",
            criteria: new[]
            {
                "Each reasoning step follows logically from the prior step or from stated premises",
                "Intermediate conclusions are consistent with each other and with the final answer",
                "The agent does not introduce unjustified assumptions in the reasoning chain",
                "The agent correctly applies relevant domain rules, constraints, or formulae cited in the reasoning",
            },
            passThreshold: passThreshold,
            judgeModel: judgeModel,
            promptId: "agenteval.reasoning_correctness.v1",
            failureSeverity: "medium");
    }

    /// <inheritdoc/>
    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Resolve the reasoning trace: prefer explicit metadata key, fall back to response scan.
        var reasoningText = ResolveReasoning(input);
        if (reasoningText is null)
            return Task.FromResult(EvalResult.Skipped(this,
                $"No reasoning trace found: '{MetadataKey}' key absent from Metadata and no reasoning-style content detected in Response."));

        // If the reasoning trace is a separate artefact (not the full response), inject it into
        // context so the judge receives it alongside the query and response.
        var judgeInput = reasoningText != input.Response
            ? input with { Context = $"[Agent reasoning trace]\n{reasoningText}\n\n{input.Context}".TrimEnd() }
            : input;

        return _inner.EvaluateAsync(judgeInput, ct);
    }

    /// <summary>
    /// Returns the reasoning text to evaluate, or <c>null</c> if no reasoning is observable.
    /// </summary>
    private static string? ResolveReasoning(EvalInput input)
    {
        // Priority 1: explicit reasoning_trace metadata key.
        if (input.Metadata is not null &&
            input.Metadata.TryGetValue(MetadataKey, out var traceObj) &&
            traceObj is string traceStr &&
            !string.IsNullOrWhiteSpace(traceStr))
        {
            return traceStr;
        }

        // Priority 2: scan response for chain-of-thought style markers.
        if (input.Response is not null)
        {
            foreach (var prefix in ReasoningPrefixes)
            {
                if (input.Response.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                    return input.Response;
            }
        }

        return null;
    }
}
