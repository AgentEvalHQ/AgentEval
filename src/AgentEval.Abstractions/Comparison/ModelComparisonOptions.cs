// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Comparison;

/// <summary>
/// Configuration options for model comparison.
/// </summary>
/// <param name="RunsPerModel">Number of test runs per model. Default: 5.</param>
/// <param name="ScoringWeights">Weights for different scoring factors.</param>
/// <param name="EnableCostAnalysis">Whether to include cost in comparison. Default: true.</param>
/// <param name="EnableStatistics">Whether to compute detailed statistics. Default: true.</param>
/// <param name="ConfidenceLevel">Confidence level for statistical intervals. Default: 0.95.</param>
/// <param name="MaxParallelism">Maximum parallel runs per model. Default: 1.</param>
/// <param name="DelayBetweenRuns">Delay between runs to avoid rate limiting.</param>
public record ModelComparisonOptions(
    int RunsPerModel = 5,
    ScoringWeights? ScoringWeights = null,
    bool EnableCostAnalysis = true,
    bool EnableStatistics = true,
    double ConfidenceLevel = 0.95,
    int MaxParallelism = 1,
    TimeSpan? DelayBetweenRuns = null)
{
    /// <summary>
    /// Default options with 5 runs per model.
    /// </summary>
    public static ModelComparisonOptions Default { get; } = new();
    
    /// <summary>
    /// Quick comparison with 3 runs per model.
    /// </summary>
    public static ModelComparisonOptions Quick { get; } = new(RunsPerModel: 3);
    
    /// <summary>
    /// Thorough comparison with 10 runs per model.
    /// </summary>
    public static ModelComparisonOptions Thorough { get; } = new(RunsPerModel: 10);
    
    /// <summary>
    /// Validates the options and throws if invalid.
    /// </summary>
    public void Validate()
    {
        if (RunsPerModel < 1)
            throw new ArgumentOutOfRangeException(nameof(RunsPerModel), RunsPerModel, "Must be at least 1.");
        
        if (MaxParallelism < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxParallelism), MaxParallelism, "Must be at least 1.");
        
        if (ConfidenceLevel <= 0.0 || ConfidenceLevel >= 1.0)
            throw new ArgumentOutOfRangeException(nameof(ConfidenceLevel), ConfidenceLevel, "Must be between 0.0 and 1.0 (exclusive).");
    }
    
    /// <summary>
    /// Gets the effective scoring weights (default if not specified).
    /// </summary>
    public ScoringWeights EffectiveScoringWeights => ScoringWeights ?? Comparison.ScoringWeights.Default;
}

/// <summary>
/// Weights for different factors in model scoring.
/// All weights should sum to 1.0 for normalized scoring.
/// </summary>
/// <param name="Quality">Weight for response quality/accuracy. Default: 0.4.</param>
/// <param name="Speed">Weight for response speed. Default: 0.2.</param>
/// <param name="Cost">Weight for cost efficiency. Default: 0.2.</param>
/// <param name="Reliability">Weight for consistency/reliability. Default: 0.2.</param>
public record ScoringWeights(
    double Quality = 0.4,
    double Speed = 0.2,
    double Cost = 0.2,
    double Reliability = 0.2)
{
    /// <summary>
    /// Default balanced weights.
    /// </summary>
    public static ScoringWeights Default { get; } = new();
    
    /// <summary>
    /// Quality-focused weights (60% quality).
    /// </summary>
    public static ScoringWeights QualityFocused { get; } = new(Quality: 0.6, Speed: 0.15, Cost: 0.1, Reliability: 0.15);
    
    /// <summary>
    /// Speed-focused weights (50% speed).
    /// </summary>
    public static ScoringWeights SpeedFocused { get; } = new(Quality: 0.25, Speed: 0.5, Cost: 0.1, Reliability: 0.15);
    
    /// <summary>
    /// Cost-focused weights (50% cost).
    /// </summary>
    public static ScoringWeights CostFocused { get; } = new(Quality: 0.25, Speed: 0.1, Cost: 0.5, Reliability: 0.15);
    
    /// <summary>
    /// Reliability-focused weights (40% reliability).
    /// </summary>
    public static ScoringWeights ReliabilityFocused { get; } = new(Quality: 0.3, Speed: 0.15, Cost: 0.15, Reliability: 0.4);
    
    /// <summary>
    /// Gets the total weight sum.
    /// </summary>
    public double TotalWeight => Quality + Speed + Cost + Reliability;
    
    /// <summary>
    /// Validates that weights sum to approximately 1.0.
    /// </summary>
    public void Validate()
    {
        if (Quality < 0 || Speed < 0 || Cost < 0 || Reliability < 0)
            throw new ArgumentException("All weights must be non-negative.");
        
        if (Math.Abs(TotalWeight - 1.0) > 0.001)
            throw new ArgumentException($"Weights must sum to 1.0, but sum is {TotalWeight:F3}.");
    }
}
