// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Models;

namespace AgentEval.Core;

/// <summary>
/// Extended interface for agents that are workflows or multi-agent systems.
/// Provides visibility into individual executor steps during execution.
/// </summary>
/// <remarks>
/// <para>
/// While <see cref="IEvaluableAgent"/> treats agents as black boxes (prompt → response),
/// this interface exposes the internal workflow structure, enabling:
/// </para>
/// <list type="bullet">
///   <item>Per-executor output capture</item>
///   <item>Step-by-step timing analysis</item>
///   <item>Tool call tracking per executor</item>
///   <item>Workflow orchestration validation</item>
/// </list>
/// <para>
/// Implement this interface for MAF Workflows, LangGraph chains, AutoGen groups,
/// or any multi-agent orchestration pattern.
/// </para>
/// </remarks>
public interface IWorkflowEvaluableAgent : IEvaluableAgent
{
    /// <summary>
    /// Executes the workflow and returns detailed per-executor results.
    /// </summary>
    /// <param name="prompt">The input prompt to process.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Workflow execution result with per-step details.</returns>
    Task<WorkflowExecutionResult> ExecuteWorkflowAsync(
        string prompt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the list of executor IDs in this workflow.
    /// May be empty if executor IDs are dynamically determined.
    /// </summary>
    IReadOnlyList<string> ExecutorIds { get; }

    /// <summary>
    /// Gets the workflow type/pattern name (e.g., "PromptChaining", "Routing", "Parallel").
    /// </summary>
    string? WorkflowType { get; }
}
