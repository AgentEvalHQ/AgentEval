// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Calibration;

/// <summary>
/// Strategy for combining multiple judge scores into a final result.
/// </summary>
/// <remarks>
/// Different strategies are appropriate for different use cases:
/// <list type="bullet">
///   <item><description><see cref="Median"/> - Robust to outliers, good default choice</description></item>
///   <item><description><see cref="Mean"/> - Simple average, sensitive to outliers</description></item>
///   <item><description><see cref="Unanimous"/> - Requires consensus, strictest validation</description></item>
///   <item><description><see cref="Weighted"/> - Weights judges by reliability</description></item>
/// </list>
/// </remarks>
public enum VotingStrategy
{
    /// <summary>
    /// Use the median score from all judges.
    /// Most robust to outliers and recommended for general use.
    /// </summary>
    Median,
    
    /// <summary>
    /// Use the arithmetic mean of all judge scores.
    /// Simple but can be affected by outlier scores.
    /// </summary>
    Mean,
    
    /// <summary>
    /// Require all judges to agree within a tolerance threshold.
    /// Strictest validation - fails if judges diverge significantly.
    /// </summary>
    Unanimous,
    
    /// <summary>
    /// Weight scores by judge reliability/confidence.
    /// Requires judge weights to be configured in options.
    /// </summary>
    Weighted
}
