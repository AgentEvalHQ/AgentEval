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
    /// Weighted overall score (0-100) across all non-skipped categories.
    /// Skipped categories are excluded and weights are renormalized.
    /// </summary>
    public double OverallScore
    {
        get
        {
            var active = CategoryResults.Where(c => !c.Skipped).ToList();
            if (active.Count == 0) return 0;
            var totalWeight = active.Sum(c => c.Weight);
            return totalWeight > 0 ? active.Sum(c => c.Score * c.Weight) / totalWeight * 1.0 : 0;
        }
    }

    /// <summary>
    /// Letter grade for the overall score.
    /// </summary>
    public string Grade => OverallScore switch
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
    public int Stars => OverallScore switch
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
    public bool Passed => OverallScore >= 70;

    /// <summary>
    /// Total execution time for the entire benchmark.
    /// </summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// Categories that need improvement (score below 70, excluding skipped).
    /// </summary>
    public IReadOnlyList<string> WeakCategories => CategoryResults
        .Where(c => !c.Skipped && c.Score < 70)
        .OrderBy(c => c.Score)
        .Select(c => c.CategoryName)
        .ToList();

    /// <summary>
    /// Categories that were skipped (e.g., agent doesn't support the required capability).
    /// </summary>
    public IReadOnlyList<string> SkippedCategories => CategoryResults
        .Where(c => c.Skipped)
        .Select(c => c.CategoryName)
        .ToList();

    /// <summary>
    /// Actionable recommendations based on the benchmark results.
    /// </summary>
    public IReadOnlyList<string> Recommendations => BuildRecommendations();

    private List<string> BuildRecommendations()
    {
        var recommendations = new List<string>();

        // Note skipped categories first
        foreach (var cat in CategoryResults.Where(c => c.Skipped))
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
}
