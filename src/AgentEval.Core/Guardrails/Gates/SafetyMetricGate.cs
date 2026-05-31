// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;

namespace AgentEval.Guardrails.Gates;

/// <summary>
/// Adapts any <see cref="ISafetyMetric"/> (e.g. <c>ToxicityMetric</c>) into an <see cref="IChatGate"/>:
/// runs the metric over the inspected text and <see cref="GateAction.Block"/>s when the metric does not pass.
/// </summary>
public sealed class SafetyMetricGate : IChatGate
{
    private readonly ISafetyMetric _metric;
    private readonly string _policyName;

    /// <summary>Wraps <paramref name="metric"/>; <paramref name="policyName"/> defaults to the metric's name.</summary>
    public SafetyMetricGate(ISafetyMetric metric, string? policyName = null)
    {
        _metric = metric ?? throw new ArgumentNullException(nameof(metric));
        _policyName = policyName ?? metric.Name;
    }

    /// <inheritdoc />
    public string PolicyName => _policyName;

    /// <inheritdoc />
    public async ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default)
    {
        // EvaluationContext.Input/Output are `required`; for a single piece of content set both to it.
        var context = new EvaluationContext { Input = text, Output = text };
        var result = await _metric.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);
        return result.Passed
            ? GateVerdict.Allow(PolicyName)
            : GateVerdict.Block(PolicyName, result.Explanation ?? "safety metric failed");
    }
}
