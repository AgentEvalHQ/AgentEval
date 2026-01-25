// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentEval.Comparison;

/// <summary>
/// Interface for comparing multiple AI models (agents) on the same test cases.
/// Enables head-to-head performance comparisons with statistical analysis.
/// </summary>
/// <remarks>
/// Model comparison runs stochastic tests on multiple models to compare:
/// - Quality (average scores)
/// - Speed (latency distributions)
/// - Cost (token usage and estimated costs)
/// - Reliability (pass rates and consistency)
/// 
/// Results are ranked using configurable weights for each dimension,
/// allowing users to optimize for different priorities (e.g., quality vs. cost).
/// Implementations should support concurrent model testing for efficiency.
/// </remarks>
public interface IModelComparer
{
    /// <summary>
    /// Compares multiple models on a single test case with stochastic analysis.
    /// Each model is run multiple times and results are aggregated and ranked.
    /// </summary>
    /// <param name="factories">
    /// Agent factories for each model to compare. Must contain at least one factory.
    /// Each factory should be configured for a different model/configuration.
    /// </param>
    /// <param name="testCase">The test case to run on all models. Cannot be null.</param>
    /// <param name="options">
    /// Comparison options including number of runs per model, scoring weights,
    /// statistical analysis settings, etc. If null, uses <see cref="ModelComparisonOptions.Default"/>.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the comparison operation.</param>
    /// <returns>
    /// Comparison result containing per-model statistics, rankings, and a winner
    /// determined by the composite score based on configured weights.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when factories or testCase is null.</exception>
    /// <exception cref="ArgumentException">Thrown when factories collection is empty.</exception>
    Task<ModelComparisonResult> CompareModelsAsync(
        IReadOnlyList<IAgentFactory> factories,
        TestCase testCase,
        ModelComparisonOptions? options = null,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Compares multiple models across multiple test cases.
    /// This is a convenience method that runs <see cref="CompareModelsAsync(IReadOnlyList{IAgentFactory}, TestCase, ModelComparisonOptions?, CancellationToken)"/>
    /// for each test case sequentially.
    /// </summary>
    /// <param name="factories">
    /// Agent factories for each model to compare. Must contain at least one factory.
    /// Each factory should be configured for a different model/configuration.
    /// </param>
    /// <param name="testCases">The test cases to run. Cannot be null or empty.</param>
    /// <param name="options">
    /// Comparison options applied to all test cases. If null, uses <see cref="ModelComparisonOptions.Default"/>.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the comparison operation.</param>
    /// <returns>
    /// List of comparison results, one for each test case, in the same order as testCases.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when factories or testCases is null.</exception>
    /// <exception cref="ArgumentException">Thrown when factories or testCases collection is empty.</exception>
    Task<IReadOnlyList<ModelComparisonResult>> CompareModelsAsync(
        IReadOnlyList<IAgentFactory> factories,
        IReadOnlyList<TestCase> testCases,
        ModelComparisonOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default implementation of <see cref="IModelComparer"/>.
/// </summary>
public class ModelComparer : IModelComparer
{
    private readonly IStochasticRunner _stochasticRunner;
    
    /// <summary>
    /// Creates a new model comparer with a stochastic runner dependency.
    /// </summary>
    /// <param name="stochasticRunner">The stochastic runner to use for running evaluations.</param>
    [ActivatorUtilitiesConstructor]
    public ModelComparer(IStochasticRunner stochasticRunner)
    {
        _stochasticRunner = stochasticRunner ?? throw new ArgumentNullException(nameof(stochasticRunner));
    }
    
    /// <summary>
    /// Creates a new model comparer (legacy constructor for backward compatibility).
    /// </summary>
    /// <param name="harness">The evaluation harness to use.</param>
    /// <param name="evaluationOptions">Optional evaluation options for each run.</param>
    [Obsolete("Use constructor with IStochasticRunner for better testability. This constructor will be removed in a future version.")]
    public ModelComparer(IEvaluationHarness harness, EvaluationOptions? evaluationOptions = null)
        : this(new StochasticRunner(harness, evaluationOptions))
    {
    }
    
    /// <inheritdoc/>
    public async Task<ModelComparisonResult> CompareModelsAsync(
        IReadOnlyList<IAgentFactory> factories,
        TestCase testCase,
        ModelComparisonOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (factories == null || factories.Count == 0)
            throw new ArgumentException("At least one factory is required.", nameof(factories));
        
        options ??= ModelComparisonOptions.Default;
        options.Validate();
        
        var stochasticOptions = new StochasticOptions(
            Runs: options.RunsPerModel,
            EnableStatisticalAnalysis: options.EnableStatistics,
            ConfidenceLevel: options.ConfidenceLevel,
            MaxParallelism: options.MaxParallelism,
            DelayBetweenRuns: options.DelayBetweenRuns);
        
        var modelResults = new List<ModelResult>();
        
        // Run each model
        foreach (var factory in factories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            var stochasticResult = await _stochasticRunner.RunStochasticTestAsync(
                factory, 
                testCase, 
                stochasticOptions, 
                cancellationToken);
            
            var avgLatency = CalculateAverageLatency(stochasticResult);
            var (avgCost, totalCost) = CalculateCosts(stochasticResult, options.EnableCostAnalysis);
            
            modelResults.Add(new ModelResult(
                ModelId: factory.ModelId,
                ModelName: factory.ModelName,
                StochasticResult: stochasticResult,
                AverageLatency: avgLatency,
                AverageCost: avgCost,
                TotalCost: totalCost));
        }
        
        // Calculate rankings
        var rankings = CalculateRankings(modelResults, options.EffectiveScoringWeights);
        
        return new ModelComparisonResult(
            TestCase: testCase,
            ModelResults: modelResults.AsReadOnly(),
            Ranking: rankings.AsReadOnly(),
            Winner: rankings[0],
            Options: options);
    }
    
    /// <inheritdoc/>
    public async Task<IReadOnlyList<ModelComparisonResult>> CompareModelsAsync(
        IReadOnlyList<IAgentFactory> factories,
        IReadOnlyList<TestCase> testCases,
        ModelComparisonOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ModelComparisonResult>();
        
        foreach (var testCase in testCases)
        {
            var result = await CompareModelsAsync(factories, testCase, options, cancellationToken);
            results.Add(result);
        }
        
        return results.AsReadOnly();
    }
    
    private static TimeSpan CalculateAverageLatency(StochasticResult result)
    {
        var totalTicks = result.IndividualResults
            .Where(r => r.Performance != null)
            .Sum(r => r.Performance!.TotalDuration.Ticks);
        
        var count = result.IndividualResults.Count(r => r.Performance != null);
        if (count == 0) return TimeSpan.Zero;
        
        return TimeSpan.FromTicks(totalTicks / count);
    }
    
    private static (decimal? avgCost, decimal? totalCost) CalculateCosts(StochasticResult result, bool enabled)
    {
        if (!enabled) return (null, null);
        
        var costs = result.IndividualResults
            .Where(r => r.Performance?.EstimatedCost != null)
            .Select(r => r.Performance!.EstimatedCost!.Value)
            .ToList();
        
        if (costs.Count == 0) return (null, null);
        
        var total = costs.Sum();
        var avg = total / costs.Count;
        
        return (avg, total);
    }
    
    private static List<ModelRanking> CalculateRankings(
        List<ModelResult> results, 
        ScoringWeights weights)
    {
        // Normalize scores for each dimension (0-100 scale)
        var qualityScores = NormalizeScores(results.Select(r => r.MeanScore).ToList(), higherIsBetter: true);
        var speedScores = NormalizeScores(
            results.Select(r => r.AverageLatency.TotalMilliseconds).ToList(), 
            higherIsBetter: false);
        var costScores = NormalizeScores(
            results.Select(r => (double)(r.AverageCost ?? 0m)).ToList(), 
            higherIsBetter: false);
        var reliabilityScores = NormalizeScores(
            results.Select(r => r.PassRate).ToList(), 
            higherIsBetter: true);
        
        var rankings = new List<ModelRanking>();
        
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            
            var qualityScore = qualityScores[i];
            var speedScore = speedScores[i];
            var costScore = costScores[i];
            var reliabilityScore = reliabilityScores[i];
            
            var compositeScore = 
                (qualityScore * weights.Quality) +
                (speedScore * weights.Speed) +
                (costScore * weights.Cost) +
                (reliabilityScore * weights.Reliability);
            
            rankings.Add(new ModelRanking(
                ModelId: r.ModelId,
                ModelName: r.ModelName,
                CompositeScore: compositeScore,
                QualityScore: qualityScore,
                SpeedScore: speedScore,
                CostScore: costScore,
                ReliabilityScore: reliabilityScore,
                Rank: 0)); // Will be set after sorting
        }
        
        // Sort by composite score (descending) and assign ranks
        rankings = rankings
            .OrderByDescending(r => r.CompositeScore)
            .Select((r, idx) => r with { Rank = idx + 1 })
            .ToList();
        
        return rankings;
    }
    
    private static List<double> NormalizeScores(List<double> values, bool higherIsBetter)
    {
        if (values.Count == 0) return new List<double>();
        if (values.Count == 1) return new List<double> { 100.0 };
        
        var min = values.Min();
        var max = values.Max();
        
        if (Math.Abs(max - min) < 0.0001)
            return values.Select(_ => 100.0).ToList();
        
        return values.Select(v =>
        {
            var normalized = (v - min) / (max - min) * 100.0;
            return higherIsBetter ? normalized : (100.0 - normalized);
        }).ToList();
    }
}
