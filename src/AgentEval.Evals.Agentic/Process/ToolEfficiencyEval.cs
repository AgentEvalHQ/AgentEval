// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;

namespace AgentEval.Evals.Agentic.Process;

/// <summary>
/// Evaluates the efficiency of an AI agent's tool usage.
/// <para>
/// Tests: did the agent make redundant calls (same tool, same arguments more than once)?
/// Did it make wasteful calls (tool called but result never used in subsequent reasoning)?
/// </para>
/// <para>
/// Implemented as an <see cref="AtomicLlmEval"/>. The prompt asks the judge to count
/// redundant and wasteful calls and compute a score of
/// <c>1.0 − (bad_calls / total_calls)</c>. Severity is intentionally lower than other
/// process evaluators: efficiency failures are optimization concerns, not correctness failures.
/// </para>
/// <para>
/// Source: derived from Azure/azure-sdk-for-python
/// sdk/evaluation/azure-ai-evaluation/azure/ai/evaluation/_evaluators/_tool_call_accuracy/tool_call_accuracy.prompty
/// License: MIT. Modifications listed in the corresponding prompt file at
/// Resources/Prompts/process/tool-efficiency.v1.md.
/// </para>
/// <para>
/// Foundry reference: <c>azureai://built-in/evaluators/tool_call_accuracy</c> (efficiency sub-dimension)
/// </para>
/// </summary>
public sealed class ToolEfficiencyEval : IEval
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
    /// Initialises a new <see cref="ToolEfficiencyEval"/>.
    /// </summary>
    /// <param name="judge">The LLM evaluator used to assess tool efficiency.</param>
    /// <param name="judgeModel">Optional judge model identifier recorded in provenance.</param>
    /// <param name="passThreshold">Score fraction (0..1) at or above which the eval passes. Defaults to 0.80.</param>
    public ToolEfficiencyEval(IEvaluator judge, string? judgeModel = null, double passThreshold = 0.80)
    {
        ArgumentNullException.ThrowIfNull(judge);
        _inner = new AtomicLlmEval(
            evaluator: judge,
            key: "tool_efficiency",
            name: "Tool Efficiency",
            category: "agentic-process",
            version: "1.0.0",
            criteria: new[]
            {
                "No tool is called twice with functionally identical arguments (no redundant calls)",
                "Every tool result is used in subsequent reasoning or the final response (no wasteful calls)",
                "Exploratory calls (different arguments after an initial probe) are not penalised",
            },
            passThreshold: passThreshold,
            judgeModel: judgeModel,
            promptId: "agenteval.tool_efficiency.v1",
            failureSeverity: "low");
    }

    /// <inheritdoc/>
    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
        _inner.EvaluateAsync(input, ct);
}
