// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;

namespace AgentEval.Evals.Agentic.Process;

/// <summary>
/// Evaluates whether an AI agent chose the right tools for a given task.
/// <para>
/// Tests: were the required tools chosen? Were any essential tools missing?
/// Were there redundant choices (same tool, same arguments)? Were acceptable
/// alternatives used in place of required tools?
/// </para>
/// <para>
/// Wraps an <see cref="AtomicLlmEval"/> with four criteria: required-tool coverage, no redundant
/// calls, alignment with the query intent, and credit for acceptable alternatives. The score is the
/// judge's overall score in <c>[0,1]</c>, with one evidence entry per criterion. The weighted formula
/// (0.60 / 0.25 / 0.15) and the <c>failure_type</c> field are in the reference prompt file and are not
/// yet applied.
/// </para>
/// <para>
/// Lineage: AgentEval's own criteria and reference prompt, modelled on the evaluator concept (name,
/// inputs and scoring dimensions) of Azure/azure-sdk-for-python
/// <c>sdk/evaluation/azure-ai-evaluation/azure/ai/evaluation/_evaluators/_tool_selection/tool_selection.prompty</c>.
/// A 2026-10-02 check found no upstream prompt text in the reference prompt file
/// <c>Resources/Prompts/process/tool-selection.v1.md</c>, which is not yet sent to the judge.
/// </para>
/// <para>
/// Foundry reference: <c>azureai://built-in/evaluators/tool_selection</c>
/// </para>
/// </summary>
public sealed class ToolSelectionEval : IEval
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
    /// Initialises a new <see cref="ToolSelectionEval"/>.
    /// </summary>
    /// <param name="judge">The LLM evaluator used to score tool selection.</param>
    /// <param name="judgeModel">Optional judge model identifier recorded in provenance.</param>
    /// <param name="passThreshold">Score fraction (0..1) at or above which the eval passes. Defaults to 0.70.</param>
    public ToolSelectionEval(IEvaluator judge, string? judgeModel = null, double passThreshold = 0.70)
    {
        ArgumentNullException.ThrowIfNull(judge);
        _inner = new AtomicLlmEval(
            evaluator: judge,
            key: "tool_selection",
            name: "Tool Selection",
            category: "agentic-process",
            version: "1.0.0",
            criteria: new[]
            {
                "All required tools (per expected_actions.required_tools) were called",
                "No tool was called redundantly (same tool + same arguments more than once)",
                "Tool calls are aligned with the user query intent",
                "Acceptable alternative tools are credited when they serve the same purpose",
            },
            passThreshold: passThreshold,
            judgeModel: judgeModel,
            promptId: "agenteval.tool_selection.v1",
            failureSeverity: "medium");
    }

    /// <inheritdoc/>
    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
        _inner.EvaluateAsync(input, ct);
}
