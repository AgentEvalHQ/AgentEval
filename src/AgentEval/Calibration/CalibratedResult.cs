// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Calibration;

/// <summary>
/// Result from a calibrated multi-judge evaluation.
/// Contains the aggregated score, agreement metrics, and individual judge votes.
/// </summary>
/// <remarks>
/// A calibrated result provides statistical confidence in the evaluation by:
/// <list type="bullet">
///   <item><description>Running the same evaluation with multiple LLM judges</description></item>
///   <item><description>Calculating agreement percentage across judges</description></item>
///   <item><description>Providing confidence intervals for the score</description></item>
/// </list>
/// </remarks>
public record CalibratedResult
{
    /// <summary>
    /// Gets the final aggregated score after applying the voting strategy.
    /// Range: 0-100 (normalized to AgentEval standard scale).
    /// </summary>
    public required double Score { get; init; }
    
    /// <summary>
    /// Gets the agreement percentage across all judges.
    /// 100% means all judges returned identical scores.
    /// Calculated as: 100 - (standard deviation / mean * 100), clamped to 0-100.
    /// </summary>
    public required double Agreement { get; init; }
    
    /// <summary>
    /// Gets the individual scores from each judge, keyed by judge name.
    /// </summary>
    public required IReadOnlyDictionary<string, double> JudgeScores { get; init; }
    
    /// <summary>
    /// Gets the lower bound of the 95% confidence interval.
    /// Null if confidence interval calculation is disabled or insufficient data.
    /// </summary>
    public double? ConfidenceLower { get; init; }
    
    /// <summary>
    /// Gets the upper bound of the 95% confidence interval.
    /// Null if confidence interval calculation is disabled or insufficient data.
    /// </summary>
    public double? ConfidenceUpper { get; init; }
    
    /// <summary>
    /// Gets the standard deviation of scores across judges.
    /// Lower values indicate higher agreement.
    /// </summary>
    public double StandardDeviation { get; init; }
    
    /// <summary>
    /// Gets the voting strategy that was used to aggregate scores.
    /// </summary>
    public VotingStrategy Strategy { get; init; }
    
    /// <summary>
    /// Gets whether all judges agreed within the consensus tolerance.
    /// Only meaningful when using <see cref="VotingStrategy.Unanimous"/>.
    /// </summary>
    public bool HasConsensus { get; init; }
    
    /// <summary>
    /// Gets the number of judges that participated in the evaluation.
    /// </summary>
    public int JudgeCount => JudgeScores.Count;
    
    /// <summary>
    /// Gets the mean score across all judges.
    /// </summary>
    public double MeanScore => JudgeScores.Count > 0 ? JudgeScores.Values.Average() : 0;
    
    /// <summary>
    /// Gets a formatted summary of the calibrated result.
    /// </summary>
    public string Summary
    {
        get
        {
            var ciText = ConfidenceLower.HasValue && ConfidenceUpper.HasValue
                ? $"95% CI: [{ConfidenceLower:F1}, {ConfidenceUpper:F1}]"
                : "CI: N/A";
            
            var consensusIcon = HasConsensus ? "✅" : "⚠️";
            
            return $"Score: {Score:F1} | Agreement: {Agreement:F0}% | {ciText} | Consensus: {consensusIcon}";
        }
    }
    
    /// <summary>
    /// Gets a detailed breakdown of judge scores as a formatted string.
    /// </summary>
    public string JudgeBreakdown
    {
        get
        {
            var lines = new List<string> { "Judge Scores:" };
            foreach (var (judge, score) in JudgeScores.OrderByDescending(kvp => kvp.Value))
            {
                lines.Add($"  {judge}: {score:F1}");
            }
            return string.Join(Environment.NewLine, lines);
        }
    }
}
