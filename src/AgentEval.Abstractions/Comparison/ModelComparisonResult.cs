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
                         $"(Quality: {r.QualityScore:F1}, Speed: {Format(r.SpeedScore)}, " +
                         $"Cost: {Format(r.CostScore)}, Reliability: {r.ReliabilityScore:F1})");
            }

            if (SpeedNotMeasuredReason is { } slow)
            {
                lines.Add("");
                lines.Add($"Speed: not measured — {slow}");
            }

            if (CostNotMeasuredReason is { } why)
            {
                lines.Add("");
                lines.Add($"Cost: not measured — {why}");
            }

            lines.Add("");
            lines.Add($"Recommendation: Use {Winner.ModelName}");
            
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>
    /// Why cost was not measured for this comparison, or <see langword="null"/> when every model is ranked on it.
    /// </summary>
    /// <remarks>
    /// A dimension is ranked only when it was measured for every model. A model without a price (or a run with
    /// <see cref="ModelComparisonOptions.EnableCostAnalysis"/> off) has no cost; earlier versions ranked it as costing
    /// $0: cost score 100, "cheapest" beside a priced model. When any model has no cost, no model is ranked on cost —
    /// ranking some models on more dimensions than others would not compare them — so every
    /// <see cref="ModelRanking.CostScore"/> is <see langword="null"/> and the composite weighs the dimensions that were
    /// measured, their weights rescaled to sum to 1. The cost each model did record stays on
    /// <see cref="ModelResult.AverageCost"/>.
    /// </remarks>
    public string? CostNotMeasuredReason
    {
        get
        {
            if (Ranking.All(r => r.CostScore is not null))
                return null;

            var composite = $"no model is ranked on cost; {CompositeNote}";
            if (!Options.EnableCostAnalysis)
                return $"cost analysis is off (EnableCostAnalysis = false), so {composite}.";

            var unpriced = ModelResults.Where(m => m.AverageCost is null).Select(m => m.ModelName).ToList();
            return unpriced.Count > 0
                ? $"no cost was recorded for {string.Join(", ", unpriced)} (no price for the model), so {composite}."
                : $"a model has no cost, so {composite}.";
        }
    }

    /// <summary>
    /// Why speed was not measured for this comparison, or <see langword="null"/> when every model is ranked on it.
    /// </summary>
    /// <remarks>
    /// A run records its latency only when the agent call returns (and performance is tracked), so a model none of
    /// whose runs completed has no latency. Earlier versions averaged that to zero and ranked the model as the fastest,
    /// speed score 100: with speed-focused weights a model that never answered could win. Speed follows the cost rule:
    /// when any model has no latency, every <see cref="ModelRanking.SpeedScore"/> is <see langword="null"/> and the
    /// composite weighs the dimensions that were measured, their weights rescaled to sum to 1. Such a model is still
    /// ranked on quality and reliability, which count every run, failed ones included.
    /// </remarks>
    public string? SpeedNotMeasuredReason
    {
        get
        {
            if (Ranking.All(r => r.SpeedScore is not null))
                return null;

            var untimed = ModelResults.Where(m => m.AverageLatency is null).Select(m => m.ModelName).ToList();
            var who = untimed.Count > 0 ? $"no run of {string.Join(", ", untimed)}" : "no run of a model";
            return $"{who} recorded a latency (a run records one only when the agent call returns), so no model is " +
                   $"ranked on speed; {CompositeNote}.";
        }
    }

    // What the composite weighed, for the not-measured reasons: quality and reliability always, speed and cost when
    // every model was ranked on them.
    private string CompositeNote
    {
        get
        {
            var measured = new List<string> { "quality" };
            if (Ranking.All(r => r.SpeedScore is not null))
                measured.Add("speed");
            if (Ranking.All(r => r.CostScore is not null))
                measured.Add("cost");
            return $"the composite weighs {string.Join(", ", measured)} and reliability, their weights rescaled to sum to 1";
        }
    }

    // Not measured prints as n/a: never as 100 or 0.
    private static string Format(double? score) => score is { } value ? value.ToString("F1") : "n/a";
}

/// <summary>
/// Results for a single model in a comparison.
/// </summary>
/// <param name="ModelId">Unique identifier for the model.</param>
/// <param name="ModelName">Human-readable model name.</param>
/// <param name="StochasticResult">Full stochastic test result.</param>
/// <param name="AverageLatency">
/// Average response latency over the runs that recorded one, or <see langword="null"/> when none did: a run records its
/// latency only when the agent call returns. Not measured is not zero (see
/// <see cref="ModelComparisonResult.SpeedNotMeasuredReason"/>).
/// </param>
/// <param name="AverageCost">Average cost per run (if available).</param>
/// <param name="TotalCost">Total cost across all runs (if available).</param>
public record ModelResult(
    string ModelId,
    string ModelName,
    StochasticResult StochasticResult,
    TimeSpan? AverageLatency,
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
/// <param name="CompositeScore">Weighted composite score (0-100) over the dimensions measured for every model.</param>
/// <param name="QualityScore">Quality/accuracy score component (0-100).</param>
/// <param name="SpeedScore">
/// Speed score component (0-100), or <see langword="null"/> when speed was not measured for this comparison: some model
/// has no latency (none of its runs completed). The composite then leaves speed out (see
/// <see cref="ModelComparisonResult.SpeedNotMeasuredReason"/>).
/// </param>
/// <param name="CostScore">
/// Cost efficiency score component (0-100), or <see langword="null"/> when cost was not measured for this comparison:
/// some model has no cost (no price for it, or cost analysis off). Not measured is not 100 or 0, and the composite
/// leaves cost out (see <see cref="ModelComparisonResult.CostNotMeasuredReason"/>).
/// </param>
/// <param name="ReliabilityScore">Reliability/consistency score component (0-100).</param>
/// <param name="Rank">Position in ranking (1 = best).</param>
public record ModelRanking(
    string ModelId,
    string ModelName,
    double CompositeScore,
    double QualityScore,
    double? SpeedScore,
    double? CostScore,
    double ReliabilityScore,
    int Rank);
