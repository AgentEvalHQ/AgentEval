// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Models;

namespace AgentEval.Comparison;

/// <summary>
/// Interface for running stochastic tests.
/// </summary>
public interface IStochasticRunner
{
    /// <summary>
    /// Run a test case multiple times with stochastic options.
    /// </summary>
    /// <param name="agent">The agent to test.</param>
    /// <param name="testCase">The test case to run.</param>
    /// <param name="options">Stochastic testing options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Stochastic test result with statistics.</returns>
    Task<StochasticResult> RunStochasticTestAsync(
        ITestableAgent agent,
        TestCase testCase,
        StochasticOptions? options = null,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Run a test case multiple times using an agent factory.
    /// Creates a fresh agent for each run.
    /// </summary>
    /// <param name="factory">Factory to create agents.</param>
    /// <param name="testCase">The test case to run.</param>
    /// <param name="options">Stochastic testing options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Stochastic test result with statistics.</returns>
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
    private readonly ITestHarness _harness;
    private readonly IStatisticsCalculator _statisticsCalculator;
    private readonly TestOptions? _testOptions;
    
    /// <summary>
    /// Creates a new stochastic runner with dependency injection.
    /// </summary>
    /// <param name="harness">The test harness to use for running individual tests.</param>
    /// <param name="statisticsCalculator">Optional statistics calculator. If null, uses default.</param>
    /// <param name="testOptions">Optional test options for each run.</param>
    public StochasticRunner(
        ITestHarness harness, 
        IStatisticsCalculator? statisticsCalculator = null,
        TestOptions? testOptions = null)
    {
        _harness = harness ?? throw new ArgumentNullException(nameof(harness));
        _statisticsCalculator = statisticsCalculator ?? DefaultStatisticsCalculator.Instance;
        _testOptions = testOptions;
    }
    
    /// <summary>
    /// Creates a new stochastic runner (legacy constructor for backward compatibility).
    /// </summary>
    /// <param name="harness">The test harness to use for running individual tests.</param>
    /// <param name="testOptions">Optional test options for each run.</param>
    [Obsolete("Use constructor with IStatisticsCalculator parameter for better testability. This constructor will be removed in a future version.")]
    public StochasticRunner(ITestHarness harness, TestOptions? testOptions)
        : this(harness, null, testOptions)
    {
    }
    
    /// <inheritdoc/>
    public Task<StochasticResult> RunStochasticTestAsync(
        ITestableAgent agent,
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
        Func<ITestableAgent> agentProvider,
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
                var result = await _harness.RunTestAsync(agent, testCase, _testOptions, cancellationToken);
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
        Func<ITestableAgent> agentProvider,
        TestCase testCase,
        SemaphoreSlim semaphore,
        TimeSpan? delay,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            var agent = agentProvider();
            var result = await _harness.RunTestAsync(agent, testCase, _testOptions, cancellationToken);
            
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
