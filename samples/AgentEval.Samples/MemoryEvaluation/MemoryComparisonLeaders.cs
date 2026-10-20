// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.Models;

namespace AgentEval.Samples;

/// <summary>
/// Who leads G7's model comparison (Benchmark Reporting), overall and on each pentagon dimension: one model, or a tie,
/// which has no winner. Kept apart from the sample's console code so the tests drive it.
/// </summary>
/// <remarks>
/// Until 2026-10 the first model listed took every tie, so two models scoring the same read as a win for whichever ran
/// first, and a dimension a model did not measure printed as 0.0%, the comparer's fill value.
/// </remarks>
internal static class MemoryComparisonLeaders
{
    /// <summary>The label the comparison table prints when more than one model shares the top score.</summary>
    public const string Tie = "tie";

    /// <summary>The label it prints when no model measured the dimension.</summary>
    public const string NotMeasured = "n/a";

    /// <summary>The baselines sharing the top overall score, in run order: one, or a tie. Empty for no baselines.</summary>
    public static IReadOnlyList<MemoryBaseline> Overall(IReadOnlyList<MemoryBaseline> baselines)
    {
        if (baselines.Count == 0)
            return [];
        var top = baselines.Max(b => b.OverallScore);
        return baselines.Where(b => b.OverallScore == top).ToList();
    }

    /// <summary>
    /// The baselines sharing the top score on <paramref name="dimension"/>, in run order. A baseline's own scores are
    /// read, not the comparer's: <c>BaselineComparer</c> fills a dimension a baseline did not measure with 0, and a
    /// dimension that was not measured is not a 0, so that baseline is not in the running. Empty when none measured it.
    /// </summary>
    public static IReadOnlyList<MemoryBaseline> OnDimension(IReadOnlyList<MemoryBaseline> baselines, string dimension)
    {
        double? best = null;
        var leaders = new List<MemoryBaseline>();
        foreach (var baseline in baselines)
        {
            if (!baseline.DimensionScores.TryGetValue(dimension, out var score))
                continue;
            if (best is null || score > best)
            {
                best = score;
                leaders = [baseline];
            }
            else if (score == best)
            {
                leaders.Add(baseline);
            }
        }
        return leaders;
    }

    /// <summary>The winner column for <paramref name="leaders"/>: the one leader's name, <see cref="Tie"/>, or <see cref="NotMeasured"/>.</summary>
    public static string Winner(IReadOnlyList<MemoryBaseline> leaders) => leaders.Count switch
    {
        0 => NotMeasured,
        1 => leaders[0].Name,
        _ => Tie,
    };

    /// <summary>
    /// True when the preset separated none of the models: the overall score and every dimension tied. A larger preset
    /// may separate them; this one gives no ranking.
    /// </summary>
    public static bool NoneSeparated(MemoryBaselineComparison comparison) =>
        Overall(comparison.Baselines).Count > 1
        && comparison.Dimensions.All(d => OnDimension(comparison.Baselines, d.DimensionName).Count > 1);
}
