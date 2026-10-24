// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.External.Models;
using AgentEval.Memory.Models;
using static AgentEval.Memory.Models.MemoryBenchmarkResult; // ComputeGrade, ComputeStars

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

        if (result.OverallAccuracy is not { } overallScore)
            throw new InvalidOperationException(
                "Cannot create a scored MemoryBaseline from an external benchmark with no scored questions.");

        var categoryResults = result.PerTypeResults
            .Where(kvp => kvp.Value.Accuracy.HasValue)
            .ToDictionary(
                kvp => kvp.Key,
                kvp => new CategoryScoreEntry
            {
                Score = kvp.Value.Accuracy!.Value,
                Grade = ComputeGrade(kvp.Value.Accuracy.Value),
                Skipped = false,
                // The score's denominator, not the questions selected: an errored or inconclusive question is not
                // behind the score, and counting it here made a 1-of-3 category read as measured on 3.
                ScenarioCount = kvp.Value.ScoredQuestions,
                UnmeasuredQueries = Math.Max(0, kvp.Value.TotalQuestions - kvp.Value.ScoredQuestions),
            });

        var dimensionScores = pentagonMapperFull?.Invoke(result.PerTypeResults, result.QuestionResults)
            ?? pentagonMapper?.Invoke(result.PerTypeResults)
            ?? new Dictionary<string, double>();

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
            // Completeness follows the configured scoring denominator, including its existing judge-failure policy.
            IsComplete = result.SelectedQuestions > 0 ? result.ScoredQuestions == result.SelectedQuestions : null,
            HasMeasuredScore = true,
            CategoryResults = categoryResults,
            DimensionScores = dimensionScores,
            Recommendations = BuildRecommendations(result),
            Tags = tags?.ToList() ?? [result.BenchmarkId, "external-benchmark"]
        };
    }

    private static List<string> BuildRecommendations(ExternalBenchmarkResult result)
    {
        var recs = new List<string>();

        // First, what the score is NOT over. The baseline keeps only scores, so without this line a run that lost
        // questions to agent errors or an inconclusive judge is stored as if every selected question were scored.
        // An agent error is never scored; under JudgeFailurePolicy.RetryThenIncorrect an inconclusive judge is scored
        // as incorrect, so the inconclusive questions left out are the rest of the gap, not InconclusiveQuestions. The rest
        // is not only judge-inconclusive: a benchmark can leave a question unrun (TypedMemEval's unrun_requires_timestamps
        // is not an agent error), so it is named for both.
        var unscored = result.SelectedQuestions - result.ScoredQuestions;
        if (unscored > 0)
        {
            recs.Add(
                $"{unscored} of {result.SelectedQuestions} selected questions are not in the score " +
                $"({result.AgentFailureQuestions} agent error(s), {unscored - result.AgentFailureQuestions} not run or without a judge verdict): " +
                $"accuracy is over the {result.ScoredQuestions} scored.");
        }

        foreach (var (typeName, typeResult) in result.PerTypeResults)
        {
            if (typeResult.Accuracy is not { } accuracy)
                continue;

            if (accuracy < 50)
                recs.Add($"{typeName}: accuracy {accuracy:F0}% is below 50% — review agent's handling of this question type.");
            else if (accuracy < 70)
                recs.Add($"{typeName}: accuracy {accuracy:F0}% is moderate — consider improving recall for this category.");
        }

        if (result.OverallAccuracy is { } overall &&
            result.TaskAveragedAccuracy is { } taskAverage &&
            overall < taskAverage - 5)
            recs.Add("Overall accuracy is lower than task-averaged — performance is weaker on high-volume question types.");

        return recs;
    }
}
