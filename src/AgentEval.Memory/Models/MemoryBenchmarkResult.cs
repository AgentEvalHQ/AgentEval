// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Memory.Models;

/// <summary>
/// Comprehensive result of a memory benchmark run, with per-category scores and an overall grade.
/// </summary>
public class MemoryBenchmarkResult
{
    /// <summary>
    /// Name of the benchmark preset that was run.
    /// </summary>
    public required string BenchmarkName { get; init; }

    /// <summary>
    /// Individual results for each category in the benchmark.
    /// </summary>
    public required IReadOnlyList<BenchmarkCategoryResult> CategoryResults { get; init; }

    /// <summary>
    /// Weighted overall score (0-100). A category the agent does not support (skipped) is excluded and the weights
    /// renormalise over the rest. A category that <b>crashed</b> (<see cref="BenchmarkCategoryResult.Errored"/>) stays
    /// in the denominator at 0.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before the split, a crash was recorded as a skip, and the weights renormalised around it. The grade could not
    /// tell "87% across the board" from "87% across the third that ran". A run failure must not raise the score;
    /// <see cref="CapabilityScore"/> keeps the renormalised view for diagnosis.
    /// </para>
    /// <para>
    /// Computed once and cached — the object is immutable after init, yet Grade/Stars/Passed each
    /// re-invoke this, so a single log statement otherwise triggered the full weighted-sum three
    /// times (PERF-03).
    /// </para>
    /// </remarks>
    public double OverallScore => _overallScore ??= ComputeOverallScore();
    private double? _overallScore;

    private double ComputeOverallScore()
    {
        // Errored categories are Skipped too (for back-compat), so select them explicitly.
        var counted = CategoryResults.Where(c => !c.Skipped || c.Errored).ToList();
        if (counted.Count == 0) return 0;
        var totalWeight = counted.Sum(c => c.Weight);
        return totalWeight > 0 ? counted.Sum(c => (c.Errored ? 0 : c.Score) * c.Weight) / totalWeight : 0;
    }

    /// <summary>
    /// Weighted score (0-100) over only the categories that produced a score: skipped and crashed ones are both
    /// excluded. It answers "how good is the agent at what was measured". It is a diagnosis, not the grade: when a
    /// category crashed it is higher than <see cref="OverallScore"/>, and the difference is the crash.
    /// </summary>
    public double CapabilityScore
    {
        get
        {
            var measured = CategoryResults.Where(c => !c.Skipped && !c.Errored).ToList();
            var totalWeight = measured.Sum(c => c.Weight);
            return totalWeight > 0 ? measured.Sum(c => c.Score * c.Weight) / totalWeight : 0;
        }
    }

    /// <summary>Questions the judge produced no score for, across all categories.</summary>
    public int UnmeasuredQueries => CategoryResults.Sum(c => c.UnmeasuredQueries);

    /// <summary>
    /// True when every category ran and every question was scored. Otherwise the scores are partial: a crashed or
    /// wholly unmeasured category counts as 0 in <see cref="OverallScore"/> (a lower bound) and is left out of
    /// <see cref="CapabilityScore"/>, and an unscored question is left out of its category. Report an incomplete run
    /// as such, never as a pass or a fail.
    /// </summary>
    public bool IsComplete => !CategoryResults.Any(c => c.Errored) && UnmeasuredQueries == 0;

    /// <summary>
    /// Categories whose run threw, or in which nothing was measured. Each counts as 0 in <see cref="OverallScore"/>.
    /// </summary>
    public IReadOnlyList<string> ErroredCategories => CategoryResults
        .Where(c => c.Errored)
        .Select(c => c.CategoryName)
        .ToList();

    /// <summary>
    /// Letter grade for the overall score.
    /// </summary>
    public string Grade => ComputeGrade(OverallScore);

    /// <summary>
    /// Computes letter grade from a score: A (≥90), B (≥80), C (≥70), D (≥60), F (&lt;60).
    /// Shared by all grade computations (overall, per-category, baselines).
    /// </summary>
    public static string ComputeGrade(double score) => score switch
    {
        >= 90 => "A",
        >= 80 => "B",
        >= 70 => "C",
        >= 60 => "D",
        _ => "F"
    };

    /// <summary>
    /// Star rating (1-5) based on overall score.
    /// </summary>
    public int Stars => ComputeStars(OverallScore);

    /// <summary>
    /// Star rating (1-5) for the given score.
    /// Shared by all star computations (overall, per-category, baselines).
    /// </summary>
    public static int ComputeStars(double score) => score switch
    {
        >= 90 => 5,
        >= 75 => 4,
        >= 60 => 3,
        >= 40 => 2,
        _ => 1
    };

    /// <summary>
    /// Whether the benchmark passed (overall score >= 70).
    /// </summary>
    public bool Passed => IsComplete && OverallScore >= 70;

    /// <summary>
    /// Total execution time for the entire benchmark.
    /// </summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// Categories that need improvement (score below 70, excluding skipped).
    /// </summary>
    public IReadOnlyList<string> WeakCategories => _weakCategories ??= CategoryResults
        .Where(c => !c.Skipped && c.Score < 70)
        .OrderBy(c => c.Score)
        .Select(c => c.CategoryName)
        .ToList();
    private IReadOnlyList<string>? _weakCategories;

    /// <summary>
    /// Categories that were skipped (e.g., agent doesn't support the required capability).
    /// </summary>
    public IReadOnlyList<string> SkippedCategories => _skippedCategories ??= CategoryResults
        .Where(c => c.Skipped)
        .Select(c => c.CategoryName)
        .ToList();
    private IReadOnlyList<string>? _skippedCategories;

    /// <summary>
    /// Actionable recommendations based on the benchmark results.
    /// </summary>
    public IReadOnlyList<string> Recommendations => BuildRecommendations();

    private List<string> BuildRecommendations()
    {
        var recommendations = new List<string>();

        // Crashed categories first: they are a run failure to fix, and they count as 0 in the score.
        foreach (var cat in CategoryResults.Where(c => c.Errored))
        {
            recommendations.Add($"{cat.CategoryName} was not measured ({cat.SkipReason ?? "error"}). It counts as 0 in the " +
                                "overall score; fix the run and re-measure.");
        }

        // Then categories the agent does not support. Only these may default to "not supported".
        foreach (var cat in CategoryResults.Where(c => c.Skipped && !c.Errored))
        {
            recommendations.Add($"{cat.CategoryName} was skipped: {cat.SkipReason ?? "not supported by this agent"}.");
        }

        var weakCategories = CategoryResults.Where(c => !c.Skipped && c.Score < 70).OrderBy(c => c.Score).ToList();

        // Then recommendations for weak (non-skipped) categories
        foreach (var cat in weakCategories)
        {
            recommendations.Add(cat.ScenarioType switch
            {
                BenchmarkScenarioType.BasicRetention =>
                    $"Basic retention score is low ({cat.Score:F0}%). Consider improving the agent's context management.",
                BenchmarkScenarioType.TemporalReasoning =>
                    $"Temporal reasoning is weak ({cat.Score:F0}%). Ensure facts include timestamps in agent prompts.",
                BenchmarkScenarioType.NoiseResilience =>
                    $"Noise resilience is poor ({cat.Score:F0}%). Consider a semantic memory provider for better signal extraction.",
                BenchmarkScenarioType.ReachBackDepth =>
                    $"Reach-back depth is limited ({cat.Score:F0}%). Increase context window or use persistent memory store.",
                BenchmarkScenarioType.CrossSession =>
                    $"Cross-session memory is weak ({cat.Score:F0}%). Implement persistent memory (vector store, Foundry, etc.).",
                BenchmarkScenarioType.ReducerFidelity =>
                    $"Reducer is losing important information ({cat.Score:F0}%). Review reducer configuration or use semantic summarization.",
                BenchmarkScenarioType.FactUpdateHandling =>
                    $"Fact update handling is poor ({cat.Score:F0}%). Ensure agent overwrites outdated facts when corrections are provided.",
                BenchmarkScenarioType.MultiTopic =>
                    $"Multi-topic memory is weak ({cat.Score:F0}%). Consider topic-based memory organization.",
                BenchmarkScenarioType.Abstention =>
                    $"Agent is hallucinating personal details ({cat.Score:F0}%). Add 'I don't know' examples to system prompt and avoid fabricating information.",
                BenchmarkScenarioType.ConflictResolution =>
                    $"Agent fails to track conflicting information ({cat.Score:F0}%). Ensure agent prioritizes the most recent statement when facts contradict.",
                BenchmarkScenarioType.MultiSessionReasoning =>
                    $"Multi-session reasoning is weak ({cat.Score:F0}%). Agent cannot synthesize information across session boundaries — needs persistent cross-session memory.",
                _ => $"{cat.CategoryName} needs improvement ({cat.Score:F0}%)."
            });
        }

        if (weakCategories.Count == 0)
            recommendations.Add("All categories performing well! Consider running the Full benchmark for deeper analysis.");

        return recommendations;
    }
}

/// <summary>
/// Result for a single benchmark category.
/// </summary>
public class BenchmarkCategoryResult
{
    /// <summary>
    /// Display name of the category.
    /// </summary>
    public required string CategoryName { get; init; }

    /// <summary>
    /// Score (0-100) for this category.
    /// </summary>
    public required double Score { get; init; }

    /// <summary>
    /// Weight of this category in the overall benchmark.
    /// </summary>
    public required double Weight { get; init; }

    /// <summary>
    /// Star rating (1-5) for this category.
    /// </summary>
    public int Stars => Score switch
    {
        >= 90 => 5,
        >= 75 => 4,
        >= 60 => 3,
        >= 40 => 2,
        _ => 1
    };

    /// <summary>
    /// The type of scenario that was run for this category.
    /// </summary>
    public required BenchmarkScenarioType ScenarioType { get; init; }

    /// <summary>
    /// Execution time for this category.
    /// </summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// Whether this category was skipped (e.g., cross-session without ISessionResettableAgent).
    /// </summary>
    public bool Skipped { get; init; }

    /// <summary>
    /// Reason the category was skipped, if applicable.
    /// </summary>
    public string? SkipReason { get; init; }

    /// <summary>
    /// Whether the category's run threw. An errored category is also <see cref="Skipped"/>, so existing readers still
    /// see that it produced no score. It differs from a legitimate skip in one place: it counts as 0 in
    /// <see cref="MemoryBenchmarkResult.OverallScore"/> instead of leaving the denominator.
    /// </summary>
    public bool Errored { get; init; }

    /// <summary>
    /// Questions in this category the judge produced no score for. They are left out of <see cref="Score"/>; any of
    /// them makes the run incomplete (<see cref="MemoryBenchmarkResult.IsComplete"/>).
    /// </summary>
    public int UnmeasuredQueries { get; init; }
}
