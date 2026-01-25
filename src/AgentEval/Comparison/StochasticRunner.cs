// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentEval.Comparison;

/// <summary>
/// Interface for running stochastic (repeated, randomized) evaluations on agents.
/// Enables statistical analysis of agent behavior across multiple runs.
/// </summary>
/// <remarks>
/// stochastic evaluation runs the same test case multiple times to:
/// - Measure reliability and consistency
/// - Identify flaky behaviors
/// - Calculate statistical confidence in results
/// - Compare performance distributions across runs
/// 
/// Implementations should support concurrent evaluation execution for performance
/// and provide detailed statistics including mean, median, confidence intervals,
/// and pass rate analysis.
/// </remarks>
public interface IStochasticRunner
{
    /// <summary>
    /// Runs a test case multiple times against the same agent instance.
    /// Use this method when evaluating stateful agents or when you want to reuse
    /// the same agent instance across all runs.
    /// </summary>
    /// <param name="agent">The agent to evaluate. Cannot be null.</param>
    /// <param name="testCase">The test case to run repeatedly. Cannot be null.</param>
    /// <param name="options">
    /// stochastic evaluation options (number of runs, parallelism, success threshold, etc.).
    /// If null, uses <see cref="StochasticOptions.Default"/>.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the stochastic evaluation run.</param>
    /// <returns>
    /// Stochastic evaluation result containing individual run results, aggregate statistics,
    /// and pass/fail determination based on the success rate threshold.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when agent or testCase is null.</exception>
    Task<StochasticResult> RunStochasticTestAsync(
        IEvaluableAgent agent,
        TestCase testCase,
        StochasticOptions? options = null,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Runs a test case multiple times, creating a fresh agent for each run.
    /// Use this method when you need isolated, independent runs without state carryover.
    /// This is the recommended approach for most stochastic evaluation scenarios.
    /// </summary>
    /// <param name="factory">Factory to create fresh agent instances. Cannot be null.</param>
    /// <param name="testCase">The test case to run repeatedly. Cannot be null.</param>
    /// <param name="options">
    /// stochastic evaluation options (number of runs, parallelism, success threshold, etc.).
    /// If null, uses <see cref="StochasticOptions.Default"/>.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the stochastic evaluation run.</param>
    /// <returns>
    /// Stochastic evaluation result containing individual run results, aggregate statistics,
    /// and pass/fail determination based on the success rate threshold.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when factory or testCase is null.</exception>
    Task<StochasticResult> RunStochasticTestAsync(
        IAgentFactory factory,
        TestCase testCase,
        StochasticOptions? options = null,
        CancellationToken cancellationToken = default);
}

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
    
    /// <summary>
    /// Creates a new stochastic runner (legacy constructor for backward compatibility).
    /// </summary>
    /// <param name="harness">The evaluation harness to use for running individual evaluations.</param>
    /// <param name="evaluationOptions">Optional evaluation options for each run.</param>
    [Obsolete("Use constructor with IStatisticsCalculator parameter for better testability. This constructor will be removed in a future version.")]
    public StochasticRunner(IEvaluationHarness harness, EvaluationOptions? evaluationOptions)
        : this(harness, statisticsCalculator: null, evaluationOptions: evaluationOptions)
    {
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
        
        if (options.MaxParallelism == 1)
        {
            // Sequential execution
            for (int i = 0; i < options.Runs; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                var agent = agentProvider();
                var result = await _harness.RunEvaluationAsync(agent, testCase, _testOptions, cancellationToken);
                results.Add(result);
                
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
            
            var completedResults = await Task.WhenAll(tasks);
            results.AddRange(completedResults);
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
