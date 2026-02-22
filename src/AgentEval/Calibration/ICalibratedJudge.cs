// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;

namespace AgentEval.Calibration;

/// <summary>
/// Interface for calibrated multi-judge evaluation.
/// Wraps multiple LLM judges to provide statistically reliable evaluations.
/// </summary>
/// <remarks>
/// Calibrated judges improve reliability by:
/// <list type="bullet">
///   <item><description>Running the same evaluation with multiple LLM judges</description></item>
///   <item><description>Aggregating scores using configurable voting strategies</description></item>
///   <item><description>Calculating agreement metrics and confidence intervals</description></item>
/// </list>
/// </remarks>
public interface ICalibratedJudge
{
    /// <summary>
    /// Evaluates content using multiple judges and returns a calibrated result.
    /// </summary>
    /// <param name="context">The evaluation context containing query, response, and optional context.</param>
    /// <param name="metricFactory">Factory function that creates a metric instance given a judge name. 
    /// The metric should be configured with the appropriate IChatClient for that judge.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>A calibrated result with aggregated score, agreement metrics, and individual judge scores.</returns>
    Task<CalibratedResult> EvaluateAsync(
        EvaluationContext context,
        Func<string, IMetric> metricFactory,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Evaluates content using multiple judges with a pre-configured metric type.
    /// Creates a new metric instance for each judge using the configured clients.
    /// </summary>
    /// <typeparam name="TMetric">The metric type to use. Must have a constructor that accepts IChatClient.</typeparam>
    /// <param name="metric">A sample metric (used to determine the type).</param>
    /// <param name="context">The evaluation context.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>A calibrated result with aggregated score and agreement metrics.</returns>
    Task<CalibratedResult> EvaluateAsync<TMetric>(
        TMetric metric,
        EvaluationContext context,
        CancellationToken cancellationToken = default) where TMetric : IMetric;
    
    /// <summary>
    /// Gets the names of all configured judges.
    /// </summary>
    IReadOnlyList<string> JudgeNames { get; }
    
    /// <summary>
    /// Gets the options used for calibration.
    /// </summary>
    CalibratedJudgeOptions Options { get; }
}
