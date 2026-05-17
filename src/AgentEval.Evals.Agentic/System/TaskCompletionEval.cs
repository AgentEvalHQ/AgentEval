// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;

namespace AgentEval.Evals.Agentic.System;

/// <summary>
/// Evaluates whether an AI agent completed the user's task end-to-end.
/// <para>
/// Wraps an <see cref="AtomicLlmEval"/> configured with the task-completion rubric.
/// Emits <c>completion_state</c> taxonomy
/// (<c>complete | partial | blocked_requires_user | blocked_requires_tool | safe_refusal | failed</c>)
/// via the judge's <c>criteria_results</c> → <see cref="EvalResult.Details"/>.
/// </para>
/// <para>
/// Source: forked from Azure/azure-sdk-for-python
/// sdk/evaluation/azure-ai-evaluation/azure/ai/evaluation/_evaluators/_task_completion/task_completion.prompty
/// License: MIT. Modifications listed in the corresponding prompt file.
/// </para>
/// </summary>
public sealed class TaskCompletionEval : IEval
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
    /// Initialises a new <see cref="TaskCompletionEval"/>.
    /// </summary>
    /// <param name="judge">The LLM evaluator used to score completion.</param>
    /// <param name="judgeModel">Optional judge model identifier recorded in provenance.</param>
    /// <param name="passThreshold">Score fraction (0..1) at or above which the eval passes. Defaults to 0.70.</param>
    public TaskCompletionEval(IEvaluator judge, string? judgeModel = null, double passThreshold = 0.70)
    {
        ArgumentNullException.ThrowIfNull(judge);
        _inner = new AtomicLlmEval(
            evaluator: judge,
            key: "task_completion",
            name: "Task Completion",
            category: "system-outcome",
            version: "1.0.0",
            criteria: new[]
            {
                "Response completes the task end-to-end",
                "External claims (e.g. data retrieval, external action) are supported by tool calls",
                "Response is actionable (not just informational)",
            },
            passThreshold: passThreshold,
            judgeModel: judgeModel,
            promptId: "agenteval.task_completion.v1",
            failureSeverity: "medium");
    }

    /// <inheritdoc/>
    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
        _inner.EvaluateAsync(input, ct);
}
