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
/// Wraps a single AgentEval <see cref="IMetric"/> as an MEAI <see cref="MEAIIEvaluator"/>,
/// enabling AgentEval metrics to be used inside MAF's <c>agent.EvaluateAsync()</c>
/// orchestration alongside MEAI and Foundry evaluators.
/// </summary>
/// <remarks>
/// This is the "light path" adapter — post-mortem evaluation of a conversation transcript.
/// For live evaluation with streaming, tool timelines, and workflow graph analysis,
/// use <see cref="AgentEval.MAF.MAFEvaluationHarness"/> (deep path).
/// </remarks>
public class AgentEvalMetricAdapter : MEAIIEvaluator
{
    private readonly IMetric _metric;

    /// <summary>
    /// Creates an adapter wrapping an AgentEval metric as an MEAI evaluator.
    /// </summary>
    public AgentEvalMetricAdapter(IMetric metric)
    {
        _metric = metric ?? throw new ArgumentNullException(nameof(metric));
    }

    /// <summary>Gets the name of the wrapped AgentEval metric.</summary>
    public string MetricName => _metric.Name;

    /// <inheritdoc/>
    public IReadOnlyCollection<string> EvaluationMetricNames => [_metric.Name];

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

        MetricResult metricResult;
        try
        {
            metricResult = await _metric.EvaluateAsync(context, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            metricResult = MetricResult.Fail(_metric.Name, $"Metric execution failed: {ex.Message}");
        }

        return ResultConverter.ToMEAI(metricResult);
    }
}
