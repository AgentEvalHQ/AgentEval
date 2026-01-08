// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Comparison;

/// <summary>
/// Configuration options for stochastic testing.
/// </summary>
/// <param name="Runs">Number of test runs. Default: 10. Minimum: 3.</param>
/// <param name="SuccessRateThreshold">Minimum pass rate to consider the test as successful (0.0-1.0). Default: 0.8 (80%).</param>
/// <param name="Seed">Optional seed for reproducibility.</param>
/// <param name="MaxParallelism">Maximum parallel runs. Default: 1 (sequential).</param>
/// <param name="DelayBetweenRuns">Delay between runs to avoid rate limiting. Default: TimeSpan.Zero.</param>
/// <param name="EnableStatisticalAnalysis">Whether to compute detailed statistics. Default: true.</param>
/// <param name="ConfidenceLevel">Confidence level for statistical intervals (0.0-1.0). Default: 0.95 (95%).</param>
public record StochasticOptions(
    int Runs = 10,
    double SuccessRateThreshold = 0.8,
    int? Seed = null,
    int MaxParallelism = 1,
    TimeSpan? DelayBetweenRuns = null,
    bool EnableStatisticalAnalysis = true,
    double ConfidenceLevel = 0.95)
{
    /// <summary>
    /// Default options with 10 runs and 80% success threshold.
    /// </summary>
    public static StochasticOptions Default { get; } = new();
    
    /// <summary>
    /// Quick validation with 5 runs and 70% threshold.
    /// </summary>
    public static StochasticOptions Quick { get; } = new(Runs: 5, SuccessRateThreshold: 0.7);
    
    /// <summary>
    /// Thorough validation with 30 runs and 90% threshold.
    /// </summary>
    public static StochasticOptions Thorough { get; } = new(Runs: 30, SuccessRateThreshold: 0.9);
    
    /// <summary>
    /// CI-optimized with 20 runs and 85% threshold.
    /// </summary>
    public static StochasticOptions CI { get; } = new(Runs: 20, SuccessRateThreshold: 0.85, MaxParallelism: 3);
    
    /// <summary>
    /// Validates the options and throws if invalid.
    /// </summary>
    public void Validate()
    {
        if (Runs < 3)
            throw new ArgumentOutOfRangeException(nameof(Runs), Runs, "Minimum 3 runs required for statistical validity.");
        
        if (SuccessRateThreshold < 0.0 || SuccessRateThreshold > 1.0)
            throw new ArgumentOutOfRangeException(nameof(SuccessRateThreshold), SuccessRateThreshold, "Must be between 0.0 and 1.0.");
        
        if (MaxParallelism < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxParallelism), MaxParallelism, "Must be at least 1.");
        
        if (ConfidenceLevel <= 0.0 || ConfidenceLevel >= 1.0)
            throw new ArgumentOutOfRangeException(nameof(ConfidenceLevel), ConfidenceLevel, "Must be between 0.0 and 1.0 (exclusive).");
    }
}
