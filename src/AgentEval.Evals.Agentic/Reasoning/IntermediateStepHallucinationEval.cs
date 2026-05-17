// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using AgentEval.Core;
using AgentEval.Evals;

namespace AgentEval.Evals.Agentic.Reasoning;

/// <summary>
/// Evaluates whether an AI agent fabricated intermediate facts — values, tool results, or
/// intermediate conclusions — that were not grounded in actual tool call outputs or user-provided
/// context.
/// <para>
/// The evaluator cross-checks claims in <see cref="EvalInput.Response"/> against:
/// <list type="number">
///   <item>The <see cref="EvalInput.ToolCalls"/> list (ground-truth for all tool-produced data).</item>
///   <item>The <see cref="EvalInput.Context"/> (documents or prior context provided to the agent).</item>
///   <item>The <see cref="EvalInput.Query"/> (facts stated by the user).</item>
/// </list>
/// If the response references information that does not appear in any of those sources AND was
/// not stated as an explicit assumption, it is classified as a hallucinated intermediate step.
/// </para>
/// <para>
/// Three hallucination failure types are assessed:
/// <list type="bullet">
///   <item><b>Fabricated tool result</b> — the response claims a tool returned a specific value
///   that does not appear in any <see cref="ToolCall.Result"/>.</item>
///   <item><b>Fabricated context</b> — the response attributes information to prior context or the
///   user's message when no such information is present in <see cref="EvalInput.Context"/> or
///   <see cref="EvalInput.Query"/>.</item>
///   <item><b>Fabricated intermediate fact</b> — an intermediate conclusion used in reasoning
///   cannot be traced to any tool result, context entry, or stated premise.</item>
/// </list>
/// </para>
/// <para>
/// When <see cref="EvalInput.Response"/> is absent, the evaluator returns
/// <see cref="EvalResult.Skipped"/> — there are no claims to assess.
/// </para>
/// <para>
/// <b>No metadata keys consumed</b> — all required inputs are standard <see cref="EvalInput"/>
/// fields (<c>Query</c>, <c>Response</c>, <c>ToolCalls</c>, <c>Context</c>).
/// </para>
/// <para>
/// <b>Cost tier: MEDIUM</b> — LLM judge cross-checks response claims against tool call results.
/// Estimated ~$0.01-0.05 per scenario (GPT-4-class judge). Cost scales with the number of tool
/// calls serialised into the judge context.
/// </para>
/// <para>
/// Source: AgentEval-original. Plan 06 §3.3 B3.4.
/// </para>
/// </summary>
public sealed class IntermediateStepHallucinationEval : IEval
{
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
    /// Initialises a new <see cref="IntermediateStepHallucinationEval"/>.
    /// </summary>
    /// <param name="judge">The LLM evaluator used to detect fabricated intermediate steps.</param>
    /// <param name="judgeModel">Optional judge model identifier recorded in provenance.</param>
    /// <param name="passThreshold">
    /// Score fraction (0..1) at or above which the eval passes. Defaults to <c>0.85</c>.
    /// High threshold is intentional: intermediate hallucinations undermine the reliability of
    /// any agent that chains reasoning steps or tool calls.
    /// </param>
    public IntermediateStepHallucinationEval(IEvaluator judge, string? judgeModel = null, double passThreshold = 0.85)
    {
        ArgumentNullException.ThrowIfNull(judge);
        _inner = new AtomicLlmEval(
            evaluator: judge,
            key: "intermediate_step_hallucination",
            name: "Intermediate Step Hallucination",
            category: "reasoning",
            version: "1.0.0",
            criteria: new[]
            {
                "No tool result values are referenced in the response unless they appear in tool_calls",
                "No context facts are attributed to prior context or user query unless they actually appear there",
                "All intermediate facts used in reasoning are traceable to tool_calls, context, or query",
                "When tool_calls are absent, the agent does not claim to have retrieved or computed external data",
            },
            passThreshold: passThreshold,
            judgeModel: judgeModel,
            promptId: "agenteval.intermediate_step_hallucination.v1",
            failureSeverity: "high");
    }

    /// <inheritdoc/>
    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Nothing to assess without a response containing claims.
        if (string.IsNullOrWhiteSpace(input.Response))
            return Task.FromResult(EvalResult.Skipped(this,
                "EvalInput.Response is absent — no agent claims to cross-check for intermediate-step hallucinations."));

        // When tool calls are present, serialise their results into the context so the LLM judge
        // can compare response claims against actual tool outputs.
        var judgeInput = BuildJudgeInput(input);

        return _inner.EvaluateAsync(judgeInput, ct);
    }

    /// <summary>
    /// Constructs the judge input, injecting tool call results and context summaries so the
    /// judge can perform cross-checking.
    /// </summary>
    private static EvalInput BuildJudgeInput(EvalInput input)
    {
        if (input.ToolCalls is null or { Count: 0 })
            return input;

        // Serialise tool call results into a structured block that the judge can reference.
        var toolResultsBlock = BuildToolResultsBlock(input.ToolCalls);
        var combinedContext = string.IsNullOrWhiteSpace(input.Context)
            ? toolResultsBlock
            : $"{toolResultsBlock}\n\n[Prior context]\n{input.Context}";

        return input with { Context = combinedContext };
    }

    /// <summary>
    /// Formats the tool call list as a labelled ground-truth block for the judge prompt.
    /// </summary>
    private static string BuildToolResultsBlock(IReadOnlyList<ToolCall> toolCalls)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Tool call results — ground truth for hallucination detection]");
        for (var i = 0; i < toolCalls.Count; i++)
        {
            var tc = toolCalls[i];
            sb.AppendLine($"Tool call {i + 1}: {tc.Name}");
            if (tc.Arguments is not null)
            {
                sb.AppendLine($"  Arguments: {JsonSerializer.Serialize(tc.Arguments)}");
            }
            sb.AppendLine($"  Result: {tc.Result ?? "(no result)"}");
        }
        return sb.ToString().TrimEnd();
    }
}
