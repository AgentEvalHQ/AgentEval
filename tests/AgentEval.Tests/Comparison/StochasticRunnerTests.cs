// Copyright (c) 2025-2026 AgentEval Contributors
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
    
    #region Test Doubles
    
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
