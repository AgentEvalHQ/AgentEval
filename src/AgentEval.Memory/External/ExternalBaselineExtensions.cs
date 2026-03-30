// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.External.Models;
using AgentEval.Memory.Models;

namespace AgentEval.Memory.External;

/// <summary>
/// Extension methods to convert external benchmark results to <see cref="MemoryBaseline"/>
/// for the shared persistence/reporting pipeline.
/// </summary>
public static class ExternalBaselineExtensions
{
    /// <summary>
    /// Converts an external benchmark result to a <see cref="MemoryBaseline"/> for storage and reporting.
    /// </summary>
    /// <remarks>
    /// Pentagon mapper that receives both per-type results and question-level detail
    /// (needed for cross-cutting dimensions like Abstention).
    /// </remarks>
    public delegate Dictionary<string, double> PentagonMapperWithQuestions(
        Dictionary<string, TypeResult> perTypeResults,
        IReadOnlyList<QuestionResult>? questionResults);

    public static MemoryBaseline ToBaseline(
        this ExternalBenchmarkResult result,
        string name,
        AgentBenchmarkConfig config,
        string? description = null,
        IReadOnlyList<string>? tags = null,
        Func<Dictionary<string, TypeResult>, Dictionary<string, double>>? pentagonMapper = null,
        PentagonMapperWithQuestions? pentagonMapperFull = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(config);

        var categoryResults = result.PerTypeResults.ToDictionary(
            kvp => kvp.Key,
            kvp => new CategoryScoreEntry
            {
                Score = kvp.Value.Accuracy,
                Grade = ComputeGrade(kvp.Value.Accuracy),
                Skipped = false,
                ScenarioCount = kvp.Value.TotalQuestions
            });

        var dimensionScores = pentagonMapperFull?.Invoke(result.PerTypeResults, result.QuestionResults)
            ?? pentagonMapper?.Invoke(result.PerTypeResults)
            ?? new Dictionary<string, double>();

        var overallScore = result.OverallAccuracy;

        return new MemoryBaseline
        {
            Id = $"bl-{Guid.NewGuid():N}"[..11],
            Name = name,
            Description = description,
            Timestamp = DateTimeOffset.UtcNow,
            ConfigurationId = config.ConfigurationId,
            AgentConfig = config,
            Benchmark = new BenchmarkExecutionInfo
            {
                Preset = result.Options.DatasetMode != null
                    ? $"{result.BenchmarkId}-{result.Options.DatasetMode}"
                    : result.BenchmarkId,
                Duration = result.Duration,
                TotalLlmCalls = result.TotalLlmCalls,
                EstimatedCostUsd = result.EstimatedCostUsd,
                BenchmarkSource = result.BenchmarkId
            },
            OverallScore = overallScore,
            Grade = ComputeGrade(overallScore),
            Stars = ComputeStars(overallScore),
            CategoryResults = categoryResults,
            DimensionScores = dimensionScores,
            Recommendations = BuildRecommendations(result),
            Tags = tags?.ToList() ?? [result.BenchmarkId, "external-benchmark"]
        };
    }

    private static string ComputeGrade(double score) => score switch
    {
        >= 90 => "A",
        >= 80 => "B",
        >= 70 => "C",
        >= 60 => "D",
        _ => "F"
    };

    private static int ComputeStars(double score) => score switch
    {
        >= 90 => 5,
        >= 75 => 4,
        >= 60 => 3,
        >= 40 => 2,
        _ => 1
    };

    private static List<string> BuildRecommendations(ExternalBenchmarkResult result)
    {
        var recs = new List<string>();

        foreach (var (typeName, typeResult) in result.PerTypeResults)
        {
            if (typeResult.Accuracy < 50)
                recs.Add($"{typeName}: accuracy {typeResult.Accuracy:F0}% is below 50% — review agent's handling of this question type.");
            else if (typeResult.Accuracy < 70)
                recs.Add($"{typeName}: accuracy {typeResult.Accuracy:F0}% is moderate — consider improving recall for this category.");
        }

        if (result.OverallAccuracy < result.TaskAveragedAccuracy - 5)
            recs.Add("Overall accuracy is lower than task-averaged — performance is weaker on high-volume question types.");

        return recs;
    }
}
