// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.RedTeam.Reporting;

namespace AgentEval.Samples;

/// <summary>Selects a bounded, presentation-friendly number of paired stochastic trials.</summary>
internal static class ReliabilityRaceRunCountSelector
{
    public const int Default = 20;

    private static readonly IReadOnlySet<int> Allowed = new HashSet<int> { 5, 10, 20, 100 };

    public static int Select(
        string? configuredValue,
        bool interactive,
        TextReader input,
        TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        if (!string.IsNullOrWhiteSpace(configuredValue))
        {
            if (int.TryParse(configuredValue, out var configured) && Allowed.Contains(configured))
            {
                output.WriteLine($"   Agent runs: {configured} per model (AGENTEVAL_RELIABILITY_RUNS)\n");
                return configured;
            }

            throw new ArgumentException(
                "AGENTEVAL_RELIABILITY_RUNS must be one of: 5, 10, 20, 100.",
                nameof(configuredValue));
        }

        if (!interactive)
        {
            output.WriteLine($"   Agent runs: {Default} per model (non-interactive default)\n");
            return Default;
        }

        output.WriteLine("   Choose the stochastic depth (agent runs per model):");
        output.WriteLine("      5    quick pulse");
        output.WriteLine("      10   short rehearsal");
        output.WriteLine("      20   recommended live demo");
        output.WriteLine("      100  evidence run with a much tighter interval");

        while (true)
        {
            output.Write($"   Agent runs/model [default {Default}]: ");
            var raw = input.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(raw))
            {
                output.WriteLine();
                return Default;
            }

            if (int.TryParse(raw, out var selected) && Allowed.Contains(selected))
            {
                output.WriteLine();
                return selected;
            }

            output.WriteLine("   Enter 5, 10, 20, or 100.\n");
        }
    }
}

/// <summary>A single paired-trial observation used by the Reliability Race sample.</summary>
internal sealed record ReliabilityRaceObservation(
    string Scenario,
    bool Correct,
    bool ToolAdherent,
    bool ToolExecutionError,
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
    int ToolErrorCount,
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
            ToolErrorCount: observations.Count(o => o.ToolExecutionError),
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

/// <summary>
/// Produces an honest reliability interpretation and an unweighted, multi-factor recommendation.
/// </summary>
internal sealed record ReliabilityRaceDecision(
    bool ReliabilityIsDraw,
    string? ReliabilityLeader,
    double ReliabilityDelta,
    bool ReliabilityIntervalsSeparate,
    IReadOnlyList<string> RecommendedWinners,
    string RecommendationReason)
{
    private const double EqualityTolerance = 1e-12;

    public bool RecommendationIsTie => RecommendedWinners.Count > 1;

    public static ReliabilityRaceDecision Create(
        ReliabilityRaceSummary first,
        ReliabilityRaceSummary second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        var reliabilityComparison = CompareHigher(first.Reliable.Estimate, second.Reliable.Estimate);
        var reliabilityIsDraw = reliabilityComparison == 0;
        var reliabilityLeader = reliabilityComparison switch
        {
            > 0 => first,
            < 0 => second,
            _ => null,
        };
        var reliabilityOther = ReferenceEquals(reliabilityLeader, first) ? second : first;

        var factors = new List<(string Name, int Comparison)>
        {
            ("correctness", CompareHigher(first.Correct.Estimate, second.Correct.Estimate)),
            ("required-tool adherence", CompareHigher(first.ToolAdherence.Estimate, second.ToolAdherence.Estimate)),
            ("exactly-one-tool efficiency", CompareHigher(first.ExactlyOneToolCall.Estimate, second.ExactlyOneToolCall.Estimate)),
            ("end-to-end reliability", reliabilityComparison),
            ("tool execution error rate", CompareLower(Rate(first.ToolErrorCount, first.Total), Rate(second.ToolErrorCount, second.Total))),
            ("error rate", CompareLower(Rate(first.ErrorCount, first.Total), Rate(second.ErrorCount, second.Total))),
        };

        AddNullableFactor(factors, "P50 latency", first.P50LatencyMs, second.P50LatencyMs);
        AddNullableFactor(factors, "P95 latency", first.P95LatencyMs, second.P95LatencyMs);
        AddNullableFactor(factors, "average tokens", first.AverageTokens, second.AverageTokens);
        AddNullableFactor(factors, "total cost", first.TotalCost, second.TotalCost);
        AddNullableFactor(factors, "cost per reliable run", first.CostPerReliableRun, second.CostPerReliableRun);

        var firstDominates = factors.All(factor => factor.Comparison >= 0)
            && factors.Any(factor => factor.Comparison > 0);
        var secondDominates = factors.All(factor => factor.Comparison <= 0)
            && factors.Any(factor => factor.Comparison < 0);

        IReadOnlyList<string> winners;
        string reason;
        if (firstDominates)
        {
            winners = [first.Label];
            reason = DominanceReason(first.Label, factors.Where(factor => factor.Comparison > 0).Select(factor => factor.Name));
        }
        else if (secondDominates)
        {
            winners = [second.Label];
            reason = DominanceReason(second.Label, factors.Where(factor => factor.Comparison < 0).Select(factor => factor.Name));
        }
        else
        {
            winners = [first.Label, second.Label];
            var firstLeads = factors.Where(factor => factor.Comparison > 0).Select(factor => factor.Name).ToArray();
            var secondLeads = factors.Where(factor => factor.Comparison < 0).Select(factor => factor.Name).ToArray();
            reason = firstLeads.Length == 0 && secondLeads.Length == 0
                ? "Every comparable factor is tied."
                : $"Neither model dominates: {LeadSummary(first.Label, firstLeads)}; {LeadSummary(second.Label, secondLeads)}.";
        }

        return new ReliabilityRaceDecision(
            ReliabilityIsDraw: reliabilityIsDraw,
            ReliabilityLeader: reliabilityLeader?.Label,
            ReliabilityDelta: Math.Abs(first.Reliable.Estimate - second.Reliable.Estimate),
            ReliabilityIntervalsSeparate: reliabilityLeader is not null
                && reliabilityLeader.Reliable.Lower > reliabilityOther.Reliable.Upper,
            RecommendedWinners: winners,
            RecommendationReason: reason);
    }

    private static int CompareHigher(double first, double second) => Compare(first, second);

    private static int CompareLower(double first, double second) => -Compare(first, second);

    private static int Compare(decimal first, decimal second) =>
        first == second ? 0 : first > second ? 1 : -1;

    private static int Compare(double first, double second) =>
        Math.Abs(first - second) <= EqualityTolerance ? 0 : first > second ? 1 : -1;

    private static double Rate(int count, int total) => total == 0 ? 0 : (double)count / total;

    private static void AddNullableFactor(
        ICollection<(string Name, int Comparison)> factors,
        string name,
        double? first,
        double? second)
    {
        if (first.HasValue && second.HasValue)
        {
            factors.Add((name, CompareLower(first.Value, second.Value)));
        }
    }

    private static void AddNullableFactor(
        ICollection<(string Name, int Comparison)> factors,
        string name,
        decimal? first,
        decimal? second)
    {
        if (first.HasValue && second.HasValue)
        {
            factors.Add((name, -Compare(first.Value, second.Value)));
        }
    }

    private static string DominanceReason(string winner, IEnumerable<string> advantages) =>
        $"{winner} is no worse on any measured factor and leads on {string.Join(", ", advantages)}.";

    private static string LeadSummary(string label, IReadOnlyCollection<string> advantages) =>
        advantages.Count == 0 ? $"{label} leads on none" : $"{label} leads on {string.Join(", ", advantages)}";
}
