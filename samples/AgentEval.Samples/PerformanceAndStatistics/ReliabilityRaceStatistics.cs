// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.RedTeam.Reporting;

namespace AgentEval.Samples;

/// <summary>A single paired-trial observation used by the Reliability Race sample.</summary>
internal sealed record ReliabilityRaceObservation(
    string Scenario,
    bool Correct,
    bool ToolAdherent,
    bool Reliable,
    int ToolCalls,
    double? LatencyMs,
    decimal? Cost,
    int? TotalTokens,
    string Output,
    string? Error);

/// <summary>Deterministic aggregation for one Reliability Race model arm.</summary>
internal sealed record ReliabilityRaceSummary(
    string Label,
    IReadOnlyList<ReliabilityRaceObservation> Observations,
    WilsonInterval Correct,
    WilsonInterval ToolAdherence,
    WilsonInterval ExactlyOneToolCall,
    WilsonInterval Reliable,
    double? P50LatencyMs,
    double? P95LatencyMs,
    double? AverageTokens,
    decimal? TotalCost,
    decimal? CostPerReliableRun,
    int ErrorCount)
{
    public int Total => Observations.Count;

    public static ReliabilityRaceSummary Create(
        string label,
        IReadOnlyList<ReliabilityRaceObservation> observations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(observations);

        var total = observations.Count;
        var reliableCount = observations.Count(o => o.Reliable);
        var latencies = observations.Where(o => o.LatencyMs.HasValue).Select(o => o.LatencyMs!.Value).ToArray();
        var tokens = observations.Where(o => o.TotalTokens.HasValue).Select(o => (double)o.TotalTokens!.Value).ToArray();
        var costs = observations.Where(o => o.Cost.HasValue).Select(o => o.Cost!.Value).ToArray();
        var totalCost = costs.Length > 0 ? costs.Sum() : (decimal?)null;

        return new ReliabilityRaceSummary(
            Label: label,
            Observations: observations,
            Correct: WilsonInterval.Compute(observations.Count(o => o.Correct), total),
            ToolAdherence: WilsonInterval.Compute(observations.Count(o => o.ToolAdherent), total),
            ExactlyOneToolCall: WilsonInterval.Compute(observations.Count(o => o.ToolCalls == 1), total),
            Reliable: WilsonInterval.Compute(reliableCount, total),
            P50LatencyMs: Percentile(latencies, 50),
            P95LatencyMs: Percentile(latencies, 95),
            AverageTokens: tokens.Length > 0 ? tokens.Average() : null,
            TotalCost: totalCost,
            CostPerReliableRun: totalCost.HasValue && reliableCount > 0 ? totalCost.Value / reliableCount : null,
            ErrorCount: observations.Count(o => o.Error is not null));
    }

    private static double? Percentile(IReadOnlyCollection<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var sorted = values.OrderBy(value => value).ToArray();
        var index = percentile / 100d * (sorted.Length - 1);
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        if (lower == upper)
        {
            return sorted[lower];
        }

        var fraction = index - lower;
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * fraction);
    }
}
