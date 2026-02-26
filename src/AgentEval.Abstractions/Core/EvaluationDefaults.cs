// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Core;

/// <summary>
/// Default values used throughout the AgentEval framework.
/// These can be overridden via configuration or constructor parameters.
/// </summary>
public static class EvaluationDefaults
{
    /// <summary>
    /// Default minimum score required to pass a test (0-100 scale).
    /// </summary>
    public const int DefaultPassingScore = 70;
    
    /// <summary>
    /// Default score assigned when evaluation parsing fails (0-100 scale).
    /// This indicates a failure in the evaluation process itself.
    /// </summary>
    public const int DefaultFailureScore = 50;
    
    /// <summary>
    /// Penalty percentage applied per extra (unexpected) tool call.
    /// </summary>
    public const double ExtraToolPenaltyPercent = 10.0;
    
    /// <summary>
    /// Penalty percentage applied when tools are called in wrong order (when strict ordering is required).
    /// </summary>
    public const double OrderPenaltyPercent = 20.0;
    
    /// <summary>
    /// Default maximum number of tool calls expected for efficiency metrics.
    /// </summary>
    public const int DefaultMaxExpectedToolCalls = 10;
    
    /// <summary>
    /// Default maximum duration expected for tool execution.
    /// </summary>
    public static readonly TimeSpan DefaultMaxToolDuration = TimeSpan.FromSeconds(30);
    
    /// <summary>
    /// Penalty percentage per duplicate tool call (potential retry).
    /// </summary>
    public const double DuplicateToolPenaltyPercent = 5.0;
    
    /// <summary>
    /// Score threshold above which a metric is considered passing.
    /// </summary>
    public const double PassingScoreThreshold = 70.0;
}
