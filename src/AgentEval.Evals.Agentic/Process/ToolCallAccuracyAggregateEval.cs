// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;

namespace AgentEval.Evals.Agentic.Process;

/// <summary>
/// Weighted aggregate of all five process / tool evaluators, producing a single
/// <strong>Tool Call Accuracy</strong> score for the agent interaction.
/// <para>
/// This is the <strong>most important evaluator in the process batch</strong>: rather than
/// reporting an opaque single score, it surfaces exactly which sub-dimension dragged the
/// overall result down (selection, input accuracy, output utilization, success, or efficiency).
/// </para>
/// <para>
/// Composition (per plan-05 §8.3 and findings-and-suggestions cross-cutting #5):
/// <list type="table">
///   <listheader><term>Sub-evaluator</term><description>Weight</description></listheader>
///   <item><term><see cref="ToolSelectionEval"/></term><description>0.25</description></item>
///   <item><term><see cref="ToolInputAccuracyEval"/></term><description>0.25</description></item>
///   <item><term><see cref="ToolOutputUtilizationEval"/></term><description>0.20</description></item>
///   <item><term><see cref="ToolCallSuccessEval"/></term><description>0.15</description></item>
///   <item><term><see cref="ToolEfficiencyEval"/></term><description>0.15</description></item>
/// </list>
/// Aggregation: <see cref="WeightedSumAggregation"/>.
/// </para>
/// <para>
/// Two constructor overloads are provided:
/// <list type="bullet">
///   <item>Full DI overload — accepts pre-built <see cref="IEval"/> instances for each
///   sub-evaluator. Use this in tests or when you need different judge configurations
///   per component.</item>
///   <item>Convenience overload — accepts a single <see cref="IEvaluator"/> and internally
///   instantiates all five sub-evaluators with it.</item>
/// </list>
/// </para>
/// <para>
/// Foundry reference: <c>azureai://built-in/evaluators/tool_call_accuracy</c>
/// </para>
/// </summary>
public sealed class ToolCallAccuracyAggregateEval : IEval
{
    private readonly CompositeEval _inner;

    /// <inheritdoc/>
    public string Key => _inner.Key;

    /// <inheritdoc/>
    public string Name => _inner.Name;

    /// <inheritdoc/>
    public string Category => _inner.Category;

    /// <inheritdoc/>
    public string Version => _inner.Version;

    /// <summary>
    /// Full dependency-injection overload. Each sub-evaluator is supplied independently,
    /// which enables per-evaluator judge configuration, mocking, and unit testing.
    /// </summary>
    /// <param name="toolSelection">Tool Selection sub-evaluator (weight 0.25).</param>
    /// <param name="toolInputAccuracy">Tool Input Accuracy sub-evaluator (weight 0.25).</param>
    /// <param name="toolOutputUtilization">Tool Output Utilization sub-evaluator (weight 0.20).</param>
    /// <param name="toolCallSuccess">Tool Call Success sub-evaluator (weight 0.15).</param>
    /// <param name="toolEfficiency">Tool Efficiency sub-evaluator (weight 0.15).</param>
    /// <param name="passThreshold">Score fraction (0..1) at or above which the aggregate passes. Defaults to 0.70.</param>
    public ToolCallAccuracyAggregateEval(
        IEval toolSelection,
        IEval toolInputAccuracy,
        IEval toolOutputUtilization,
        IEval toolCallSuccess,
        IEval toolEfficiency,
        double passThreshold = 0.70)
    {
        ArgumentNullException.ThrowIfNull(toolSelection);
        ArgumentNullException.ThrowIfNull(toolInputAccuracy);
        ArgumentNullException.ThrowIfNull(toolOutputUtilization);
        ArgumentNullException.ThrowIfNull(toolCallSuccess);
        ArgumentNullException.ThrowIfNull(toolEfficiency);

        _inner = new CompositeEval(
            key: "tool_call_accuracy",
            name: "Tool Call Accuracy",
            category: "agentic-process",
            version: "1.0.0",
            components: new[]
            {
                new EvalComponent(toolSelection,         Weight: 0.25),
                new EvalComponent(toolInputAccuracy,     Weight: 0.25),
                new EvalComponent(toolOutputUtilization, Weight: 0.20),
                new EvalComponent(toolCallSuccess,       Weight: 0.15),
                new EvalComponent(toolEfficiency,        Weight: 0.15),
            },
            aggregation: WeightedSumAggregation.Instance,
            threshold: passThreshold);
    }

    /// <summary>
    /// Convenience overload. Instantiates all five sub-evaluators using the same
    /// <paramref name="judge"/>, with default configurations.
    /// </summary>
    /// <param name="judge">The LLM evaluator shared across all sub-evaluators.</param>
    /// <param name="judgeModel">Optional judge model identifier recorded in provenance.</param>
    /// <param name="passThreshold">Score fraction (0..1) at or above which the aggregate passes. Defaults to 0.70.</param>
    public ToolCallAccuracyAggregateEval(
        IEvaluator judge,
        string? judgeModel = null,
        double passThreshold = 0.70)
        : this(
            toolSelection:         new ToolSelectionEval(judge, judgeModel),
            toolInputAccuracy:     new ToolInputAccuracyEval(judge, judgeModel),
            toolOutputUtilization: new ToolOutputUtilizationEval(judge, judgeModel),
            toolCallSuccess:       new ToolCallSuccessEval(judge, judgeModel),
            toolEfficiency:        new ToolEfficiencyEval(judge, judgeModel),
            passThreshold:         passThreshold)
    {
    }

    /// <inheritdoc/>
    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
        _inner.EvaluateAsync(input, ct);
}
