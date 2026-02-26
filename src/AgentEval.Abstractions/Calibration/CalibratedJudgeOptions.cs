// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Calibration;

/// <summary>
/// Configuration options for calibrated judge evaluation.
/// </summary>
public class CalibratedJudgeOptions
{
    /// <summary>
    /// Gets or sets the voting strategy for combining judge scores.
    /// Default: <see cref="VotingStrategy.Median"/>.
    /// </summary>
    public VotingStrategy Strategy { get; set; } = VotingStrategy.Median;
    
    /// <summary>
    /// Gets or sets the tolerance for unanimous consensus.
    /// Judges must agree within this many points for <see cref="VotingStrategy.Unanimous"/>.
    /// Default: 10.0 points.
    /// </summary>
    public double ConsensusTolerance { get; set; } = 10.0;
    
    /// <summary>
    /// Gets or sets the timeout for each individual judge evaluation.
    /// Default: 120 seconds.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(120);
    
    /// <summary>
    /// Gets or sets whether to calculate confidence intervals.
    /// Default: true.
    /// </summary>
    public bool CalculateConfidenceInterval { get; set; } = true;
    
    /// <summary>
    /// Gets or sets the confidence level for interval calculation.
    /// Default: 0.95 (95% confidence).
    /// </summary>
    public double ConfidenceLevel { get; set; } = 0.95;
    
    /// <summary>
    /// Gets or sets the maximum number of judges to run in parallel.
    /// Default: 3.
    /// </summary>
    public int MaxParallelJudges { get; set; } = 3;
    
    /// <summary>
    /// Gets or sets whether to continue if one judge fails.
    /// If true, uses remaining judge scores. If false, throws on any failure.
    /// Default: true.
    /// </summary>
    public bool ContinueOnJudgeFailure { get; set; } = true;
    
    /// <summary>
    /// Gets or sets the minimum number of successful judges required.
    /// Default: 1.
    /// </summary>
    public int MinimumJudgesRequired { get; set; } = 1;
    
    /// <summary>
    /// Gets or sets weights for each judge when using <see cref="VotingStrategy.Weighted"/>.
    /// Key: judge name, Value: weight (will be normalized to sum to 1.0).
    /// If null or empty, all judges are weighted equally.
    /// </summary>
    public Dictionary<string, double>? JudgeWeights { get; set; }
    
    /// <summary>
    /// Gets the default options instance.
    /// </summary>
    public static CalibratedJudgeOptions Default => new();
    
    /// <summary>
    /// Validates the options and throws if invalid.
    /// </summary>
    public void Validate()
    {
        if (ConsensusTolerance < 0)
            throw new ArgumentException("ConsensusTolerance must be non-negative.", nameof(ConsensusTolerance));
        
        if (ConfidenceLevel is < 0 or > 1)
            throw new ArgumentException("ConfidenceLevel must be between 0 and 1.", nameof(ConfidenceLevel));
        
        if (MaxParallelJudges < 1)
            throw new ArgumentException("MaxParallelJudges must be at least 1.", nameof(MaxParallelJudges));
        
        if (MinimumJudgesRequired < 1)
            throw new ArgumentException("MinimumJudgesRequired must be at least 1.", nameof(MinimumJudgesRequired));
    }
}
