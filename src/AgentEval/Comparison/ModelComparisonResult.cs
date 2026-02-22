// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Models;

namespace AgentEval.Comparison;

/// <summary>
/// Result of comparing multiple models on a test case.
/// </summary>
/// <param name="TestCase">The test case that was run.</param>
/// <param name="ModelResults">Results for each model.</param>
/// <param name="Ranking">Models ranked by composite score (best first).</param>
/// <param name="Winner">The recommended model based on weighted scoring.</param>
/// <param name="Options">Options used for the comparison.</param>
public record ModelComparisonResult(
    TestCase TestCase,
    IReadOnlyList<ModelResult> ModelResults,
    IReadOnlyList<ModelRanking> Ranking,
    ModelRanking Winner,
    ModelComparisonOptions Options)
{
    /// <summary>
    /// Gets a formatted summary of the comparison.
    /// </summary>
    public string Summary
    {
        get
        {
            var lines = new List<string>
            {
                $"🏆 Model Comparison Results for: {TestCase.Name}",
                $"   Test: \"{TestCase.Input}\"",
                "",
                "Rankings:",
            };
            
            for (int i = 0; i < Ranking.Count; i++)
            {
                var r = Ranking[i];
                var medal = i switch { 0 => "🥇", 1 => "🥈", 2 => "🥉", _ => $"#{i + 1}" };
                lines.Add($"   {medal} {r.ModelName} - Score: {r.CompositeScore:F1} " +
                         $"(Quality: {r.QualityScore:F1}, Speed: {r.SpeedScore:F1}, " +
                         $"Cost: {r.CostScore:F1}, Reliability: {r.ReliabilityScore:F1})");
            }
            
            lines.Add("");
            lines.Add($"Recommendation: Use {Winner.ModelName}");
            
            return string.Join(Environment.NewLine, lines);
        }
    }
}

/// <summary>
/// Results for a single model in a comparison.
/// </summary>
/// <param name="ModelId">Unique identifier for the model.</param>
/// <param name="ModelName">Human-readable model name.</param>
/// <param name="StochasticResult">Full stochastic test result.</param>
/// <param name="AverageLatency">Average response latency.</param>
/// <param name="AverageCost">Average cost per run (if available).</param>
/// <param name="TotalCost">Total cost across all runs (if available).</param>
public record ModelResult(
    string ModelId,
    string ModelName,
    StochasticResult StochasticResult,
    TimeSpan AverageLatency,
    decimal? AverageCost,
    decimal? TotalCost)
{
    /// <summary>
    /// Pass rate for this model.
    /// </summary>
    public double PassRate => StochasticResult.Statistics.PassRate;
    
    /// <summary>
    /// Mean score for this model.
    /// </summary>
    public double MeanScore => StochasticResult.Statistics.MeanScore;
    
    /// <summary>
    /// Standard deviation of scores.
    /// </summary>
    public double StandardDeviation => StochasticResult.Statistics.StandardDeviation;
}

/// <summary>
/// Ranking information for a model.
/// </summary>
/// <param name="ModelId">Unique identifier for the model.</param>
/// <param name="ModelName">Human-readable model name.</param>
/// <param name="CompositeScore">Weighted composite score (0-100).</param>
/// <param name="QualityScore">Quality/accuracy score component (0-100).</param>
/// <param name="SpeedScore">Speed score component (0-100).</param>
/// <param name="CostScore">Cost efficiency score component (0-100).</param>
/// <param name="ReliabilityScore">Reliability/consistency score component (0-100).</param>
/// <param name="Rank">Position in ranking (1 = best).</param>
public record ModelRanking(
    string ModelId,
    string ModelName,
    double CompositeScore,
    double QualityScore,
    double SpeedScore,
    double CostScore,
    double ReliabilityScore,
    int Rank);
