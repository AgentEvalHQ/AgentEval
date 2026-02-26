// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Models;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;

namespace AgentEval.Comparison;

/// <summary>
/// Default implementation of <see cref="IStochasticRunner"/>.
/// </summary>
public class StochasticRunner : IStochasticRunner
{
    private readonly IEvaluationHarness _harness;
    private readonly IStatisticsCalculator _statisticsCalculator;
    private readonly EvaluationOptions? _testOptions;
    
    /// <summary>
    /// Creates a new stochastic runner with dependency injection.
    /// </summary>
    /// <param name="harness">The evaluation harness to use for running individual evaluations.</param>
    /// <param name="statisticsCalculator">Optional statistics calculator. If null, uses default.</param>
    /// <param name="evaluationOptions">Optional evaluation options for each run.</param>
    [ActivatorUtilitiesConstructor]
    public StochasticRunner(
        IEvaluationHarness harness, 
        IStatisticsCalculator? statisticsCalculator = null,
        EvaluationOptions? evaluationOptions = null)
    {
        _harness = harness ?? throw new ArgumentNullException(nameof(harness));
        _statisticsCalculator = statisticsCalculator ?? DefaultStatisticsCalculator.Instance;
        _testOptions = evaluationOptions;
    }
    

    
    /// <inheritdoc/>
    public Task<StochasticResult> RunStochasticTestAsync(
        IEvaluableAgent agent,
        TestCase testCase,
        StochasticOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return RunStochasticTestInternalAsync(
            () => agent,
            testCase,
            options ?? StochasticOptions.Default,
            cancellationToken);
    }
    
    /// <inheritdoc/>
    public Task<StochasticResult> RunStochasticTestAsync(
        IAgentFactory factory,
        TestCase testCase,
        StochasticOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return RunStochasticTestInternalAsync(
            factory.CreateAgent,
            testCase,
            options ?? StochasticOptions.Default,
            cancellationToken);
    }
    
    private async Task<StochasticResult> RunStochasticTestInternalAsync(
        Func<IEvaluableAgent> agentProvider,
        TestCase testCase,
        StochasticOptions options,
        CancellationToken cancellationToken)
    {
        options.Validate();
        
        var results = new List<TestResult>();
        var random = options.Seed.HasValue ? new Random(options.Seed.Value) : new Random();
        var stopwatch = Stopwatch.StartNew();
        
        if (options.MaxParallelism == 1)
        {
            // Sequential execution
            for (int i = 0; i < options.Runs; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                var agent = agentProvider();
                var result = await _harness.RunEvaluationAsync(agent, testCase, _testOptions, cancellationToken);
                results.Add(result);
                
                // Report progress if callback is provided
                if (options.OnProgress != null)
                {
                    var avgDuration = stopwatch.Elapsed / (i + 1);
                    var remaining = TimeSpan.FromTicks(avgDuration.Ticks * (options.Runs - i - 1));
                    
                    options.OnProgress(new StochasticProgress(
                        CurrentRun: i + 1,
                        TotalRuns: options.Runs,
                        LastResult: result,
                        Elapsed: stopwatch.Elapsed,
                        EstimatedRemaining: remaining));
                }
                
                if (options.DelayBetweenRuns.HasValue && options.DelayBetweenRuns.Value > TimeSpan.Zero)
                {
                    await Task.Delay(options.DelayBetweenRuns.Value, cancellationToken);
                }
            }
        }
        else
        {
            // Parallel execution with throttling
            var semaphore = new SemaphoreSlim(options.MaxParallelism);
            var tasks = new List<Task<TestResult>>();
            
            for (int i = 0; i < options.Runs; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                tasks.Add(RunSingleTestWithThrottlingAsync(
                    agentProvider,
                    testCase,
                    semaphore,
                    options.DelayBetweenRuns,
                    cancellationToken));
            }
            
            // For parallel execution, report progress as tasks complete
            if (options.OnProgress != null)
            {
                var completedCount = 0;
                while (completedCount < tasks.Count)
                {
                    var completed = await Task.WhenAny(tasks);
                    completedCount++;
                    var result = await completed;
                    results.Add(result);
                    tasks.Remove(completed);
                    
                    var avgDuration = stopwatch.Elapsed / completedCount;
                    var remaining = TimeSpan.FromTicks(avgDuration.Ticks * (options.Runs - completedCount));
                    
                    options.OnProgress(new StochasticProgress(
                        CurrentRun: completedCount,
                        TotalRuns: options.Runs,
                        LastResult: result,
                        Elapsed: stopwatch.Elapsed,
                        EstimatedRemaining: remaining));
                }
            }
            else
            {
                var completedResults = await Task.WhenAll(tasks);
                results.AddRange(completedResults);
            }
        }
        
        // Calculate statistics
        var scores = results.Select(r => r.Score).ToList();
        var passResults = results.Select(r => r.Passed).ToList();
        
        var statistics = options.EnableStatisticalAnalysis
            ? _statisticsCalculator.CreateStatistics(scores, passResults, options.ConfidenceLevel)
            : CreateMinimalStatistics(scores, passResults);
        
        bool passed = statistics.PassRate >= options.SuccessRateThreshold;
        
        return new StochasticResult(
            TestCase: testCase,
            IndividualResults: results.AsReadOnly(),
            Statistics: statistics,
            Options: options,
            Passed: passed);
    }
    
    private async Task<TestResult> RunSingleTestWithThrottlingAsync(
        Func<IEvaluableAgent> agentProvider,
        TestCase testCase,
        SemaphoreSlim semaphore,
        TimeSpan? delay,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            var agent = agentProvider();
            var result = await _harness.RunEvaluationAsync(agent, testCase, _testOptions, cancellationToken);
            
            if (delay.HasValue && delay.Value > TimeSpan.Zero)
            {
                await Task.Delay(delay.Value, cancellationToken);
            }
            
            return result;
        }
        finally
        {
            semaphore.Release();
        }
    }
    
    private StochasticStatistics CreateMinimalStatistics(
        IReadOnlyList<int> scores,
        IReadOnlyList<bool> passResults)
    {
        var doubleScores = scores.Select(s => (double)s).ToList();
        
        return new StochasticStatistics(
            PassRate: _statisticsCalculator.CalculatePassRate(passResults),
            MeanScore: _statisticsCalculator.Mean(doubleScores),
            MedianScore: _statisticsCalculator.Median(doubleScores),
            StandardDeviation: 0,
            MinScore: scores.Count > 0 ? scores.Min() : 0,
            MaxScore: scores.Count > 0 ? scores.Max() : 0,
            Percentile25: 0,
            Percentile75: 0,
            Percentile95: 0,
            ConfidenceInterval: null,
            SampleSize: scores.Count);
    }
}
