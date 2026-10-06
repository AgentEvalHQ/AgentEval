// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;

namespace AgentEval.Evals.Agentic.Process;

/// <summary>
/// Evaluates whether the AI agent used the tool outputs it received in its subsequent
/// reasoning and final response.
/// <para>
/// Tests: did the agent reference or act on the data returned by each tool call?
/// Did it ignore tool results and hallucinate the answer instead?
/// Field-level <c>usage_mappings[]</c> (which tool-output fields were used vs. ignored) are
/// specified in the rubric the judge is sent, but the result does not keep them (it keeps the score, criteria
/// and evidence).
/// </para>
/// <para>
/// Wraps an <see cref="AtomicLlmEval"/> configured with four tool-output-utilization criteria.
/// The score is the judge's score in <c>[0,1]</c>; the rubric asks for the per-call average (1.0 full,
/// 0.5 partial, 0.0 ignored), and the code does not recompute it.
/// </para>
/// <para>
/// Lineage: AgentEval's own criteria and reference prompt, modelled on the evaluator concept (name,
/// inputs and scoring dimensions) of Azure/azure-sdk-for-python
/// <c>sdk/evaluation/azure-ai-evaluation/azure/ai/evaluation/_evaluators/_tool_output_utilization/tool_output_utilization.prompty</c>.
/// A 2026-10-02 check found no upstream prompt text in the rubric file
/// <c>Resources/Prompts/process/tool-output-utilization.v1.md</c>, which the judge is sent as its system prompt.
/// </para>
/// <para>
/// Foundry reference: <c>azureai://built-in/evaluators/tool_output_utilization</c>
/// </para>
/// </summary>
public sealed class ToolOutputUtilizationEval : IEval
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
    /// Initialises a new <see cref="ToolOutputUtilizationEval"/>.
    /// </summary>
    /// <param name="judge">The LLM evaluator used to assess output utilization.</param>
    /// <param name="judgeModel">Optional judge model identifier recorded in provenance.</param>
    /// <param name="passThreshold">Score fraction (0..1) at or above which the eval passes. Defaults to 0.70.</param>
    public ToolOutputUtilizationEval(IEvaluator judge, string? judgeModel = null, double passThreshold = 0.70)
    {
        ArgumentNullException.ThrowIfNull(judge);
        _inner = new AtomicLlmEval(
            evaluator: judge,
            key: "tool_output_utilization",
            name: "Tool Output Utilization",
            category: "agentic-process",
            version: "1.1.0",
            criteria: new[]
            {
                "Data returned by tool calls is referenced or acted upon in subsequent reasoning or final response",
                "No tool result is silently ignored when it contains information relevant to the query",
                "Chained tool calls use outputs from prior calls as inputs (grounding chain maintained)",
                "Agent does not hallucinate data that should have come from a tool result",
            },
            passThreshold: passThreshold,
            judgeModel: judgeModel,
            promptId: "agenteval.tool_output_utilization.v1",
            failureSeverity: "medium") { JudgeSeesToolData = JudgeToolData.ToolCalls | JudgeToolData.ToolDefinitions };
    }

    /// <inheritdoc/>
    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
        _inner.EvaluateAsync(input, ct);
}
