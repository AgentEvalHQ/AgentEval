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
/// Wraps an <see cref="AtomicLlmEval"/> with a weighted rubric:
/// required-tool coverage (0.60 weight) − redundancy penalty (0.25) + acceptable-alternative
/// bonus (0.15). Emits <c>score</c> in <c>[0,1]</c> plus structured <c>evidence[]</c>
/// and <c>failure_type</c> via the judge's criteria results.
/// </para>
/// <para>
/// Source: forked from Azure/azure-sdk-for-python
/// sdk/evaluation/azure-ai-evaluation/azure/ai/evaluation/_evaluators/_tool_selection/tool_selection.prompty
/// License: MIT. Modifications listed in the corresponding prompt file at
/// Resources/Prompts/process/tool-selection.v1.md.
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
