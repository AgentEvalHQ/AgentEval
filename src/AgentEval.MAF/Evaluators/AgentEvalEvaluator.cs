// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using AgentEval.Core;

using AgentEvalEvaluationContext = AgentEval.Core.EvaluationContext;
using MEAIEvaluationContext = Microsoft.Extensions.AI.Evaluation.EvaluationContext;
using MEAIEvaluationResult = Microsoft.Extensions.AI.Evaluation.EvaluationResult;
using MEAIIEvaluator = Microsoft.Extensions.AI.Evaluation.IEvaluator;

namespace AgentEval.MAF.Evaluators;

/// <summary>
/// Composite evaluator that bundles multiple AgentEval <see cref="IMetric"/> instances
/// into a single MEAI <see cref="MEAIIEvaluator"/>. Each metric produces a named metric
/// in the returned MEAI EvaluationResult.
/// </summary>
/// <remarks>
/// All metrics share the same <see cref="AgentEvalEvaluationContext"/> built from
/// the conversation transcript. Metrics are run sequentially to avoid contention
/// on the judge LLM.
/// </remarks>
public class AgentEvalEvaluator : MEAIIEvaluator
{
    private readonly IReadOnlyList<IMetric> _metrics;
    private readonly IReadOnlyCollection<string> _evaluationMetricNames;

    /// <summary>
    /// Creates a composite evaluator from a collection of AgentEval metrics.
    /// </summary>
    public AgentEvalEvaluator(IEnumerable<IMetric> metrics)
    {
        _metrics = (metrics ?? throw new ArgumentNullException(nameof(metrics))).ToList();
        if (_metrics.Count == 0)
            throw new ArgumentException("At least one metric must be provided.", nameof(metrics));
        _evaluationMetricNames = _metrics.Select(m => m.Name).ToList().AsReadOnly();
    }

    /// <summary>Gets the number of metrics in this evaluator.</summary>
    public int MetricCount => _metrics.Count;

    /// <summary>Gets the names of all included metrics.</summary>
    public IEnumerable<string> MetricNames => _metrics.Select(m => m.Name);

    /// <inheritdoc/>
    public IReadOnlyCollection<string> EvaluationMetricNames => _evaluationMetricNames;

    /// <inheritdoc/>
    public async ValueTask<MEAIEvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse response,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<MEAIEvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        var input = ConversationExtractor.ExtractLastUserMessage(messages);
        var output = response.Text ?? "";

        var context = new AgentEvalEvaluationContext
        {
            Input = input,
            Output = output,
            Context = AdditionalContextHelper.ExtractRAGContext(additionalContext),
            GroundTruth = AdditionalContextHelper.ExtractGroundTruth(additionalContext),
            ToolUsage = ConversationExtractor.ExtractToolUsage(messages, response),
            ExpectedTools = AdditionalContextHelper.ExtractExpectedTools(additionalContext),
        };

        var result = new MEAIEvaluationResult();
        foreach (var metric in _metrics)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var metricResult = await metric.EvaluateAsync(context, cancellationToken);
                ResultConverter.AddToEvaluationResult(result, metricResult);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var failedResult = MetricResult.Fail(
                    metric.Name,
                    $"Metric execution failed: {ex.Message}");
                ResultConverter.AddToEvaluationResult(result, failedResult);
            }
        }

        return result;
    }
}
