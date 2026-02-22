// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Assertions;
using AgentEval.Comparison;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests.Comparison;

public class StochasticAssertionsTests
{
    private static StochasticResult CreateResult(
        double passRate, 
        double meanScore = 80, 
        double stdDev = 5,
        double successThreshold = 0.8,
        int minScore = 70,
        int maxScore = 90)
    {
        var individualResults = new List<TestResult>();
        var passCount = (int)(10 * passRate);
        
        for (int i = 0; i < 10; i++)
        {
            individualResults.Add(new TestResult
            {
                TestName = $"Test_{i}",
                Passed = i < passCount,
                Score = 80,
                Details = "test"
            });
        }
        
        var stats = new StochasticStatistics(
            PassRate: passRate,
            MeanScore: meanScore,
            MedianScore: meanScore,
            StandardDeviation: stdDev,
            MinScore: minScore,
            MaxScore: maxScore,
            Percentile25: 75,
            Percentile75: 85,
            Percentile95: 88,
            ConfidenceInterval: new ConfidenceInterval(75, 85, 0.95),
            SampleSize: 10);
        
        var options = new StochasticOptions(SuccessRateThreshold: successThreshold);
        var testCase = new TestCase { Name = "Test", Input = "input" };
        
        return new StochasticResult(
            TestCase: testCase,
            IndividualResults: individualResults,
            Statistics: stats,
            Options: options,
            Passed: passRate >= successThreshold);
    }
    
    [Fact]
    public void HavePassRateAtLeast_WhenMet_DoesNotThrow()
    {
        var result = CreateResult(passRate: 0.9);
        var assertions = new StochasticAssertions(result);
        
        var exception = Record.Exception(() => assertions.HavePassRateAtLeast(0.8));
        
        Assert.Null(exception);
    }
    
    [Fact]
    public void HavePassRateAtLeast_WhenNotMet_Throws()
    {
        var result = CreateResult(passRate: 0.7);
        var assertions = new StochasticAssertions(result);
        
        Assert.Throws<StochasticAssertionException>(() => assertions.HavePassRateAtLeast(0.8));
    }
    
    [Fact]
    public void HavePassRateAtLeast_UsesOptionsThresholdByDefault()
    {
        var result = CreateResult(passRate: 0.7, successThreshold: 0.6);
        var assertions = new StochasticAssertions(result);
        
        var exception = Record.Exception(() => assertions.HavePassRateAtLeast());
        
        Assert.Null(exception);
    }
    
    [Fact]
    public void HaveMeanScoreAtLeast_WhenMet_DoesNotThrow()
    {
        var result = CreateResult(passRate: 0.9, meanScore: 85);
        var assertions = new StochasticAssertions(result);
        
        var exception = Record.Exception(() => assertions.HaveMeanScoreAtLeast(80));
        
        Assert.Null(exception);
    }
    
    [Fact]
    public void HaveMeanScoreAtLeast_WhenNotMet_Throws()
    {
        var result = CreateResult(passRate: 0.9, meanScore: 75);
        var assertions = new StochasticAssertions(result);
        
        Assert.Throws<StochasticAssertionException>(() => assertions.HaveMeanScoreAtLeast(80));
    }
    
    [Fact]
    public void HaveStandardDeviationAtMost_WhenMet_DoesNotThrow()
    {
        var result = CreateResult(passRate: 0.9, stdDev: 5);
        var assertions = new StochasticAssertions(result);
        
        var exception = Record.Exception(() => assertions.HaveStandardDeviationAtMost(10));
        
        Assert.Null(exception);
    }
    
    [Fact]
    public void HaveStandardDeviationAtMost_WhenNotMet_Throws()
    {
        var result = CreateResult(passRate: 0.9, stdDev: 15);
        var assertions = new StochasticAssertions(result);
        
        Assert.Throws<StochasticAssertionException>(() => assertions.HaveStandardDeviationAtMost(10));
    }
    
    [Fact]
    public void HaveMinScoreAtLeast_WhenMet_DoesNotThrow()
    {
        var result = CreateResult(passRate: 0.9, minScore: 70);
        var assertions = new StochasticAssertions(result);
        
        var exception = Record.Exception(() => assertions.HaveMinScoreAtLeast(60));
        
        Assert.Null(exception);
    }
    
    [Fact]
    public void HaveMinScoreAtLeast_WhenNotMet_Throws()
    {
        var result = CreateResult(passRate: 0.9, minScore: 50);
        var assertions = new StochasticAssertions(result);
        
        Assert.Throws<StochasticAssertionException>(() => assertions.HaveMinScoreAtLeast(60));
    }
    
    [Fact]
    public void HaveNoFailures_WhenAllPassed_DoesNotThrow()
    {
        var result = CreateResult(passRate: 1.0);
        var assertions = new StochasticAssertions(result);
        
        var exception = Record.Exception(() => assertions.HaveNoFailures());
        
        Assert.Null(exception);
    }
    
    [Fact]
    public void HaveNoFailures_WhenSomeFailed_Throws()
    {
        var result = CreateResult(passRate: 0.9);
        var assertions = new StochasticAssertions(result);
        
        Assert.Throws<StochasticAssertionException>(() => assertions.HaveNoFailures());
    }
    
    [Fact]
    public void Chaining_Works()
    {
        var result = CreateResult(passRate: 0.9, meanScore: 85, stdDev: 5);
        
        var exception = Record.Exception(() => 
            new StochasticAssertions(result)
                .HavePassRateAtLeast(0.8)
                .HaveMeanScoreAtLeast(80)
                .HaveStandardDeviationAtMost(10));
        
        Assert.Null(exception);
    }
    
    [Fact]
    public void Should_ExtensionMethod_Works()
    {
        var result = CreateResult(passRate: 0.9);
        
        var exception = Record.Exception(() => 
            result.Should().HavePassRateAtLeast(0.8));
        
        Assert.Null(exception);
    }
}
