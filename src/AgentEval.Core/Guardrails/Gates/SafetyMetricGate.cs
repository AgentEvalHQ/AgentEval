// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;

namespace AgentEval.Guardrails.Gates;

/// <summary>
/// Adapts any <see cref="ISafetyMetric"/> (e.g. <c>ToxicityMetric</c>) into an <see cref="IChatGate"/>:
/// runs the metric over the inspected text and <see cref="GateAction.Block"/>s when the metric does not pass.
/// </summary>
/// <remarks>
/// The gate has only the inspected text to give the metric. A metric that needs a retrieved context or a reference
/// answer (<c>GroundednessMetric</c>) is refused when the gate is built: it could never be measured here, so the gate
/// would block every message. A metric that is not measured at run time for any other reason still blocks
/// (fail-closed), with a reason that says it was not measured rather than that it failed.
/// </remarks>
public sealed class SafetyMetricGate : IChatGate
{
    private readonly ISafetyMetric _metric;
    private readonly string _policyName;

    /// <summary>Wraps <paramref name="metric"/>; <paramref name="policyName"/> defaults to the metric's name.</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="metric"/> needs a retrieved context or a reference answer, which a chat gate never has.
    /// </exception>
    public SafetyMetricGate(ISafetyMetric metric, string? policyName = null)
    {
        _metric = metric ?? throw new ArgumentNullException(nameof(metric));
        _policyName = policyName ?? metric.Name;

        var needsContext = metric is IRAGMetric { RequiresContext: true } || metric.Categories.HasFlag(MetricCategory.RequiresContext);
        var needsReference = metric is IRAGMetric { RequiresGroundTruth: true } || metric.Categories.HasFlag(MetricCategory.RequiresGroundTruth);
        if (needsContext || needsReference)
        {
            throw new ArgumentException(
                $"'{metric.Name}' needs {(needsContext ? "a retrieved context" : "a reference answer")}, and a chat gate has only " +
                "the text it inspects: the metric could never be measured here, so the gate would block every message. " +
                "Use a metric that judges the text alone (toxicity, bias, misinformation), or evaluate this one where the " +
                "context is available.",
                nameof(metric));
        }
    }

    /// <inheritdoc />
    public string PolicyName => _policyName;

    /// <inheritdoc />
    public async ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default)
    {
        // EvaluationContext.Input/Output are `required`; for a single piece of content set both to it.
        var context = new EvaluationContext { Input = text, Output = text };
        var result = await _metric.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);
        if (result.Passed)
        {
            return GateVerdict.Allow(PolicyName);
        }

        // Not measured blocks too (fail-closed: nothing showed the text safe), and the reason says which it was.
        return GateVerdict.Block(PolicyName, result.Measured
            ? result.Explanation ?? "safety metric failed"
            : $"not measured, so not shown safe: {result.Explanation ?? "the metric measured nothing"}");
    }
}
