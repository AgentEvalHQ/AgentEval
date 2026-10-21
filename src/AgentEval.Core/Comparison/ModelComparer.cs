// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Models;
using AgentEval.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace AgentEval.Comparison;

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
    

    
    /// <inheritdoc/>
    public async Task<ModelComparisonResult> CompareModelsAsync(
        IReadOnlyList<IAgentFactory> factories,
        TestCase testCase,
        ModelComparisonOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (factories == null || factories.Count == 0)
            throw new ArgumentException("At least one factory is required.", nameof(factories));
        
        TestCaseValidator.Validate(testCase);
        
        options ??= ModelComparisonOptions.Default;
        options.Validate();

        // Weights that rest only on cost, with cost analysis off, can rank nothing: refuse before any (paid) run, not after.
        var weights = options.EffectiveScoringWeights;
        if (!options.EnableCostAnalysis && weights.Quality + weights.Speed + weights.Reliability <= 0)
            throw new ArgumentException(
                "The scoring weights fall only on cost and cost analysis is off (EnableCostAnalysis = false), so no " +
                "dimension the comparison measures carries weight. Turn cost analysis on or weigh another dimension.",
                nameof(options));
        
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
        
        // Calculate rankings. When the runs leave nothing weighted to rank on (weights only on speed/cost and a model
        // without them), the exception still carries what the runs measured: they may have been paid for.
        // A model none of whose runs was measured (every run errored, or its judge produced no verdict) has no quality
        // to rank: it is left out of the ranking rather than ranked on a placeholder 0, and stays in ModelResults.
        List<ModelRanking> rankings;
        try
        {
            var rankable = modelResults.Where(m => m.StochasticResult.MeasuredCount > 0).ToList();
            if (rankable.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No model can be ranked: none of the runs of {string.Join(", ", modelResults.Select(m => m.ModelName))} " +
                    "was measured (every run errored, or the judge produced no verdict). The models' results are in this " +
                    "exception's Data[\"ModelResults\"].");
            }
            rankings = CalculateRankings(rankable, weights);
        }
        catch (InvalidOperationException ex)
        {
            ex.Data["ModelResults"] = modelResults.AsReadOnly();
            throw;
        }
        
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
    
    // Null when no run recorded a latency (none returned): not measured, never zero. Zero ranked such a model fastest.
    private static TimeSpan? CalculateAverageLatency(StochasticResult result)
    {
        var totalTicks = result.IndividualResults
            .Where(r => r.Performance != null)
            .Sum(r => r.Performance!.TotalDuration.Ticks);

        var count = result.IndividualResults.Count(r => r.Performance != null);
        if (count == 0) return null;

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
        
        return (avg, costs.Count == result.IndividualResults.Count ? total : null);
    }
    
    private static List<ModelRanking> CalculateRankings(
        List<ModelResult> results, 
        ScoringWeights weights)
    {
        // Normalize scores for each dimension (0-100 scale). Quality is the mean over the measured runs (a run that errored
        // or got no verdict has no score). Reliability is the share of ALL runs that passed: a run that did not deliver a
        // verdict counts against it, so a model that crashes cannot out-rank a reliable one on the runs it finished.
        // Only measured models are ranked (the caller filters on MeasuredCount), so every MeanScore has a value; .Value
        // throws rather than rank on a made-up 0 if that ever changes.
        var qualityScores = NormalizeScores(results.Select(r => r.MeanScore!.Value).ToList(), higherIsBetter: true);
        var reliabilityScores = NormalizeScores(
            results.Select(r => r.StochasticResult.IndividualResults.Count > 0
                ? (double)r.StochasticResult.PassedCount / r.StochasticResult.IndividualResults.Count
                : 0).ToList(),
            higherIsBetter: true);

        // A dimension is scored only when it was measured for every model; scoring only the models that have it would
        // rank the models on different dimension sets.
        // Speed and cost come from the runs that returned: an unpriced model used to be read as $0 (cost score 100,
        // "cheapest" beside a priced one), and a model none of whose runs returned as latency 0 (speed score 100,
        // "fastest" — with speed-focused weights it could win). When any model lacks one, no model is scored on it and
        // the composite rescales the other weights to sum to 1 (ModelComparisonResult.SpeedNotMeasuredReason /
        // CostNotMeasuredReason say so).
        var speedMeasured = results.All(r => r.AverageLatency.HasValue);
        var speedScores = speedMeasured
            ? NormalizeScores(results.Select(r => r.AverageLatency!.Value.TotalMilliseconds).ToList(), higherIsBetter: false)
            : null;
        var costMeasured = results.All(r => r.AverageCost.HasValue);
        var costScores = costMeasured
            ? NormalizeScores(results.Select(r => (double)r.AverageCost!.Value).ToList(), higherIsBetter: false)
            : null;

        var unmeasuredWeight = (speedMeasured ? 0 : weights.Speed) + (costMeasured ? 0 : weights.Cost);
        var rescale = 1.0;
        if (unmeasuredWeight > 0)
        {
            var measuredWeight = weights.Quality + weights.Reliability +
                                 (speedMeasured ? weights.Speed : 0) + (costMeasured ? weights.Cost : 0);
            if (measuredWeight <= 0)
            {
                var unmeasured = string.Join(" and ", new[] { speedMeasured ? null : "speed", costMeasured ? null : "cost" }
                    .Where(d => d is not null));
                throw new InvalidOperationException(
                    $"No measured dimension carries weight: the scoring weights fall only on {unmeasured}, not measured " +
                    "for every model (a model has no price or cost analysis is off; or none of a model's runs returned, " +
                    "so it has no latency). Give a measured dimension a weight, or measure every model on it. The " +
                    "models' measured results are in this exception's Data[\"ModelResults\"].");
            }
            rescale = (measuredWeight + unmeasuredWeight) / measuredWeight;
        }

        var rankings = new List<ModelRanking>();

        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];

            var qualityScore = qualityScores[i];
            double? speedScore = speedScores?[i];
            double? costScore = costScores?[i];
            var reliabilityScore = reliabilityScores[i];

            var compositeScore = rescale * (
                (qualityScore * weights.Quality) +
                (speedScore is { } speed ? speed * weights.Speed : 0) +
                (costScore is { } cost ? cost * weights.Cost : 0) +
                (reliabilityScore * weights.Reliability));

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
