// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using MEAIEvaluationResult = Microsoft.Extensions.AI.Evaluation.EvaluationResult;
using Microsoft.Extensions.AI.Evaluation;

namespace AgentEval.MAF.Evaluators;

/// <summary>
/// Converts between AgentEval <see cref="MetricResult"/> and MEAI evaluation result types.
/// </summary>
/// <remarks>
/// AgentEval uses 0–100 scale. MEAI uses 1–5 for <see cref="NumericMetric"/>.
/// Conversion uses <see cref="ScoreNormalizer.ToOneToFive"/>.
/// The original 0–100 score is preserved in the metric's interpretation reason.
/// </remarks>
public static class ResultConverter
{
    /// <summary>
    /// Converts a single AgentEval <see cref="MetricResult"/> to an MEAI evaluation result.
    /// </summary>
    public static MEAIEvaluationResult ToMEAI(MetricResult metricResult)
    {
        var result = new MEAIEvaluationResult();
        AddToEvaluationResult(result, metricResult);
        return result;
    }

    /// <summary>
    /// Adds an AgentEval <see cref="MetricResult"/> as a named metric inside an existing MEAI result.
    /// Call multiple times for composite evaluators.
    /// </summary>
    public static void AddToEvaluationResult(MEAIEvaluationResult result, MetricResult metricResult)
    {
        var meaiScore = ScoreNormalizer.ToOneToFive(metricResult.Score);

        // Build reason string preserving original 0-100 score
        var reason = $"AgentEval score: {metricResult.Score:F0}/100 " +
            $"({ScoreNormalizer.Interpret(metricResult.Score)})";

        if (!string.IsNullOrEmpty(metricResult.Explanation))
            reason += $" — {metricResult.Explanation}";

        // NumericMetric(name, value, reason)
        var numericMetric = new NumericMetric(metricResult.MetricName, meaiScore, reason);

        // Set interpretation: rating + pass/fail
        var rating = metricResult.Score switch
        {
            >= 90 => EvaluationRating.Exceptional,
            >= 75 => EvaluationRating.Good,
            >= 50 => EvaluationRating.Average,
            >= 25 => EvaluationRating.Poor,
            _ => EvaluationRating.Poor
        };

        numericMetric.Interpretation = new EvaluationMetricInterpretation(
            rating,
            failed: !metricResult.Passed,
            reason: reason);

        result.Metrics[metricResult.MetricName] = numericMetric;
    }
}
