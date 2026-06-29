// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using AgentEval.Evals;

using MEAIIEvaluator = Microsoft.Extensions.AI.Evaluation.IEvaluator;
using MEAIEvaluationContext = Microsoft.Extensions.AI.Evaluation.EvaluationContext;
using MEAIEvaluationResult = Microsoft.Extensions.AI.Evaluation.EvaluationResult;

namespace AgentEval.MAF.Evaluators;

/// <summary>
/// Adapts an AgentEval <see cref="IEval"/> — including a full <c>CompositeEval</c> such as an
/// <c>AgenticBenchmark</c> preset — into a Microsoft.Extensions.AI.Evaluation
/// <see cref="MEAIIEvaluator"/>, so an entire weighted composite tree can be run as a single
/// evaluator through MAF's <c>agent.EvaluateAsync(...)</c>.
/// </summary>
/// <remarks>
/// <para>
/// Where <see cref="AgentEvalEvaluator"/> bundles flat metrics, this runs AgentEval's composite engine
/// (weighted aggregation + thresholds + a real verdict tree) behind the MEAI interface MAF consumes.
/// </para>
/// <para>
/// The composite's sub-evaluators (e.g. the AgenticBenchmark tool sub-evals) are LLM-judged: they
/// grade the query + response text, so they work over MAF's evaluation feature even when only the
/// final response is forwarded. (To also let <i>code-based</i> tool metrics see the calls, run this
/// through <see cref="AgentEvalAgentEvaluator"/>, which forwards the full conversation.)
/// </para>
/// <para>
/// The rich <see cref="EvalResult"/> tree the composite produces is captured in
/// <see cref="CapturedResults"/> so callers can render it (HTML/PDF) with the full hierarchy intact —
/// the flat MEAI <see cref="MEAIEvaluationResult"/> returned to MAF is only for MAF's pass/fail rollup.
/// </para>
/// </remarks>
public sealed class AgentEvalCompositeEvaluator : MEAIIEvaluator
{
    private readonly IEval _composite;
    private readonly List<EvalResult> _captured = [];

    /// <summary>Creates an MEAI evaluator that runs <paramref name="composite"/> per evaluated item.</summary>
    public AgentEvalCompositeEvaluator(IEval composite) =>
        _composite = composite ?? throw new ArgumentNullException(nameof(composite));

    /// <summary>The <see cref="EvalResult"/> tree(s) produced — one per evaluated item, in call order.</summary>
    public IReadOnlyList<EvalResult> CapturedResults => _captured;

    /// <inheritdoc/>
    public IReadOnlyCollection<string> EvaluationMetricNames => [_composite.Name];

    /// <inheritdoc/>
    public async ValueTask<MEAIEvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse response,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<MEAIEvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        var query = ConversationExtractor.ExtractLastUserMessage(messages);
        var output = response.Text ?? string.Empty;

        var input = new EvalInput(Query: query, Response: output);
        EvalResult tree = await _composite.EvaluateAsync(input, cancellationToken).ConfigureAwait(false);
        _captured.Add(tree);

        // Flatten to MEAI metrics so MAF's AgentEvaluationResults gets a pass/fail rollup: the
        // composite root carries the overall verdict, and each atomic leaf is surfaced too.
        var result = new MEAIEvaluationResult();
        AddMetric(result, tree, isRoot: true);
        foreach (var leaf in EnumerateAtomicLeaves(tree))
            AddMetric(result, leaf, isRoot: false);

        return result;
    }

    private static void AddMetric(MEAIEvaluationResult result, EvalResult node, bool isRoot)
    {
        // AgentEval EvalScore.Value is 0..1; MEAI NumericMetric convention is 1..5.
        var meaiValue = 1.0 + Math.Clamp(node.Score.Value, 0, 1) * 4.0;
        var pct = node.Score.Value * 100.0;
        var reason = $"AgentEval score: {pct:F0}/100 ({node.Score.Label}, severity {node.Score.Severity})";

        var metric = new NumericMetric(isRoot ? $"{node.Metric.Name} (overall)" : node.Metric.Name, meaiValue, reason)
        {
            Interpretation = new EvaluationMetricInterpretation(
                rating: pct switch
                {
                    >= 90 => EvaluationRating.Exceptional,
                    >= 75 => EvaluationRating.Good,
                    >= 50 => EvaluationRating.Average,
                    _ => EvaluationRating.Poor,
                },
                failed: !node.Score.Passed,
                reason: reason),
        };

        // Disambiguate when two leaves share a metric name.
        var key = metric.Name;
        var i = 1;
        while (result.Metrics.ContainsKey(key))
            key = $"{metric.Name} #{++i}";
        result.Metrics[key] = metric;
    }

    private static IEnumerable<EvalResult> EnumerateAtomicLeaves(EvalResult node)
    {
        var subs = node.Details.SubResults;
        if (subs is null || subs.Count == 0)
        {
            yield return node;
            yield break;
        }

        foreach (var child in subs)
            foreach (var leaf in EnumerateAtomicLeaves(child))
                yield return leaf;
    }
}
