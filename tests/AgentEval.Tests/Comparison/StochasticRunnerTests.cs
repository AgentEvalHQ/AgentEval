// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Comparison;
using AgentEval.Core;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests.Comparison;

public class StochasticRunnerTests
{
    private readonly MockEvaluationHarness _harness;
    private readonly StochasticRunner _runner;
    
    public StochasticRunnerTests()
    {
        _harness = new MockEvaluationHarness();
        _runner = new StochasticRunner(_harness, statisticsCalculator: null);
    }
    
    [Fact]
    public async Task RunStochasticTestAsync_OnProgress_CalledForEachRun()
    {
        // Arrange
        var progressReports = new List<StochasticProgress>();
        var options = new StochasticOptions(
            Runs: 3,
            OnProgress: progress => progressReports.Add(progress));
        
        var agent = new MockAgent();
        var testCase = new TestCase { Name = "Test", Input = "input" };
        
        // Act
        await _runner.RunStochasticTestAsync(agent, testCase, options);
        
        // Assert
        Assert.Equal(3, progressReports.Count);
        Assert.Equal(1, progressReports[0].CurrentRun);
        Assert.Equal(2, progressReports[1].CurrentRun);
        Assert.Equal(3, progressReports[2].CurrentRun);
        Assert.All(progressReports, p => Assert.Equal(3, p.TotalRuns));
    }
    
    [Fact]
    public async Task RunStochasticTestAsync_OnProgress_ContainsLastResult()
    {
        // Arrange
        var progressReports = new List<StochasticProgress>();
        var options = new StochasticOptions(
            Runs: 3,
            OnProgress: progress => progressReports.Add(progress));
        
        var agent = new MockAgent();
        var testCase = new TestCase { Name = "Test", Input = "input" };
        
        // Act
        await _runner.RunStochasticTestAsync(agent, testCase, options);
        
        // Assert
        Assert.All(progressReports, p => Assert.NotNull(p.LastResult));
    }
    
    [Fact]
    public async Task RunStochasticTestAsync_OnProgress_ElapsedTimeIncreases()
    {
        // Arrange
        var progressReports = new List<StochasticProgress>();
        var options = new StochasticOptions(
            Runs: 3,
            OnProgress: progress => progressReports.Add(progress));
        
        var agent = new MockAgent();
        var testCase = new TestCase { Name = "Test", Input = "input" };
        
        // Act
        await _runner.RunStochasticTestAsync(agent, testCase, options);
        
        // Assert - elapsed time should not decrease
        for (int i = 1; i < progressReports.Count; i++)
        {
            Assert.True(
                progressReports[i].Elapsed >= progressReports[i - 1].Elapsed,
                "Elapsed time should not decrease between progress reports");
        }
    }
    
    [Fact]
    public async Task RunStochasticTestAsync_OnProgress_EstimatedRemainingDecreases()
    {
        // Arrange
        var progressReports = new List<StochasticProgress>();
        var options = new StochasticOptions(
            Runs: 5,
            OnProgress: progress => progressReports.Add(progress));
        
        var agent = new MockAgent();
        var testCase = new TestCase { Name = "Test", Input = "input" };
        
        // Act
        await _runner.RunStochasticTestAsync(agent, testCase, options);
        
        // Assert - estimated remaining should decrease (or stay same)
        var lastRemaining = progressReports.Last().EstimatedRemaining;
        Assert.NotNull(lastRemaining);
        Assert.Equal(TimeSpan.Zero, lastRemaining.Value);
    }
    
    [Fact]
    public async Task RunStochasticTestAsync_WithoutOnProgress_WorksNormally()
    {
        // Arrange
        var options = new StochasticOptions(Runs: 3);
        var agent = new MockAgent();
        var testCase = new TestCase { Name = "Test", Input = "input" };
        
        // Act
        var result = await _runner.RunStochasticTestAsync(agent, testCase, options);
        
        // Assert
        Assert.NotNull(result);
        Assert.Equal(3, result.IndividualResults.Count);
    }
    
    [Fact]
    public async Task RunStochasticTestAsync_Parallel_WithOnProgress_CollectsAllRuns()
    {
        // Regression for BUG-01: in the parallel branch the loop bound
        // `while (completedCount < tasks.Count)` was mutated by `tasks.Remove`,
        // so the counters converged at the midpoint and ~half the runs were
        // silently dropped (corrupting every downstream statistic). This only
        // fires when MaxParallelism > 1 AND OnProgress is set.

        // Arrange
        var progressReports = new List<StochasticProgress>();
        var options = new StochasticOptions(
            Runs: 10,
            MaxParallelism: 4,
            OnProgress: progress => progressReports.Add(progress));

        var agent = new MockAgent();
        var testCase = new TestCase { Name = "Test", Input = "input" };

        // Act
        var result = await _runner.RunStochasticTestAsync(agent, testCase, options);

        // Assert - all 10 runs collected and reported, none dropped
        Assert.Equal(10, result.IndividualResults.Count);
        Assert.Equal(10, progressReports.Count);
        Assert.Equal(10, progressReports[^1].CurrentRun);
        Assert.All(progressReports, p => Assert.Equal(10, p.TotalRuns));
        Assert.Equal(TimeSpan.Zero, progressReports[^1].EstimatedRemaining);
    }

    [Fact]
    public async Task RunStochasticTestAsync_WithFactory_OnProgressCalled()
    {
        // Arrange
        var progressReports = new List<StochasticProgress>();
        var options = new StochasticOptions(
            Runs: 3,
            OnProgress: progress => progressReports.Add(progress));
        
        var factory = new MockAgentFactory();
        var testCase = new TestCase { Name = "Test", Input = "input" };
        
        // Act
        await _runner.RunStochasticTestAsync(factory, testCase, options);
        
        // Assert
        Assert.Equal(3, progressReports.Count);
    }
    
    [Fact]
    public async Task RunStochasticTestAsync_Parallel_OnFault_CancelsAndObservesSiblings()
    {
        // PERF-02: when one parallel run faults, the siblings must be cancelled and awaited (not
        // left running unobserved). In the progress path the first fault is detected early; the
        // runner then cancels the linked CTS so the delaying siblings observe cancellation before
        // the original exception is rethrown.
        var harness = new FaultThenDelayHarness();
        var runner = new StochasticRunner(harness, statisticsCalculator: null);
        var options = new StochasticOptions(
            Runs: 4,
            MaxParallelism: 4,
            OnProgress: _ => { }); // progress path → early fault detection
        var agent = new MockAgent();
        var testCase = new TestCase { Name = "Test", Input = "input" };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunStochasticTestAsync(agent, testCase, options));

        // The 3 non-faulting siblings observed cancellation by the time the call returned. Without
        // the linked-CTS fix they would still be delaying, unobserved (CancelledCount would be 0).
        Assert.Equal(3, harness.CancelledCount);
    }

    #region Test Doubles

    /// <summary>Faults on the first invocation; every other invocation delays (cancellably) so the
    /// sibling-cancellation behaviour of the parallel runner can be observed (PERF-02).</summary>
    private sealed class FaultThenDelayHarness : IEvaluationHarness
    {
        private int _calls;
        private int _cancelled;
        public int CancelledCount => _cancelled;

        public Task<TestResult> RunEvaluationAsync(
            IEvaluableAgent agent,
            TestCase testCase,
            EvaluationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var idx = Interlocked.Increment(ref _calls); // 1-based
            if (idx == 1)
                throw new InvalidOperationException("first run faulted");
            return DelayThenResultAsync(testCase, cancellationToken);
        }

        private async Task<TestResult> DelayThenResultAsync(TestCase testCase, CancellationToken ct)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _cancelled);
                throw;
            }
            return new TestResult { TestName = testCase.Name, Passed = true, Score = 100, ActualOutput = "out" };
        }
    }

    private class MockEvaluationHarness : IEvaluationHarness
    {
        public Task<TestResult> RunEvaluationAsync(
            IEvaluableAgent agent,
            TestCase testCase,
            EvaluationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new TestResult
            {
                TestName = testCase.Name,
                Passed = true,
                Score = 100,
                ActualOutput = "output"
            });
        }
    }
    
    private class MockAgent : IEvaluableAgent
    {
        public string Name => "MockAgent";
        
        public Task<AgentResponse> InvokeAsync(
            string prompt,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AgentResponse
            {
                Text = "output"
            });
        }
    }
    
    private class MockAgentFactory : IAgentFactory
    {
        public string ModelId => "mock-model";
        public string ModelName => "MockModel";
        public ModelConfiguration? Configuration => null;
        
        public IEvaluableAgent CreateAgent() => new MockAgent();
    }
    
    #endregion
}
