// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Comparison;
using AgentEval.Models;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Output;

public class TableFormatterTests
{
    private static StochasticResult CreateTestResult(
        double passRate = 0.8, 
        double meanScore = 80,
        int sampleSize = 10)
    {
        var individualResults = new List<TestResult>();
        var passCount = (int)(sampleSize * passRate);
        var baseTime = DateTimeOffset.UtcNow;
        
        for (int i = 0; i < sampleSize; i++)
        {
            var duration = TimeSpan.FromMilliseconds(100 + i * 10);
            individualResults.Add(new TestResult
            {
                TestName = $"Test_{i}",
                Passed = i < passCount,
                Score = (int)meanScore,
                Details = "test",
                Performance = new PerformanceMetrics
                {
                    StartTime = baseTime,
                    EndTime = baseTime.Add(duration),
                    PromptTokens = 50,
                    CompletionTokens = 50,
                    EstimatedCost = 0.001m
                }
            });
        }
        
        var stats = new StochasticStatistics(
            PassRate: passRate,
            MeanScore: meanScore,
            MedianScore: meanScore,
            StandardDeviation: 5,
            MinScore: (int)(meanScore - 10),
            MaxScore: (int)(meanScore + 10),
            Percentile25: meanScore - 5,
            Percentile75: meanScore + 5,
            Percentile95: meanScore + 8,
            ConfidenceInterval: new ConfidenceInterval(meanScore - 5, meanScore + 5, 0.95),
            SampleSize: sampleSize);
        
        var options = new StochasticOptions(SuccessRateThreshold: 0.8);
        var testCase = new TestCase { Name = "Test", Input = "input" };
        
        return new StochasticResult(
            TestCase: testCase,
            IndividualResults: individualResults,
            Statistics: stats,
            Options: options,
            Passed: passRate >= 0.8);
    }
    
    [Fact]
    public void PrintTable_WritesToOutput()
    {
        var result = CreateTestResult();
        using var sw = new StringWriter();
        var options = new OutputOptions { Writer = sw };
        
        TableFormatter.PrintTable(result, "Test Table", options);
        
        var output = sw.ToString();
        Assert.Contains("Test Table", output);
        Assert.Contains("Score", output);
        Assert.Contains("Duration", output);
    }
    
    [Fact]
    public void ToTableString_ReturnsFormattedString()
    {
        var result = CreateTestResult();
        
        var tableStr = result.ToTableString("Test Table");
        
        Assert.Contains("Test Table", tableStr);
        Assert.Contains("Min", tableStr);
        Assert.Contains("Max", tableStr);
        Assert.Contains("Mean", tableStr);
    }
    
    [Fact]
    public void OutputOptions_Default_ShowsMainColumns()
    {
        var options = OutputOptions.Default;
        
        Assert.True(options.ShowScore);
        Assert.True(options.ShowPassRate);
        Assert.True(options.ShowDuration);
        Assert.True(options.ShowTokens);
        Assert.True(options.ShowCost);
        Assert.True(options.ShowToolCalls);
    }
    
    [Fact]
    public void OutputOptions_Minimal_HidesOptionalColumns()
    {
        var options = OutputOptions.Minimal;
        
        Assert.True(options.ShowScore);
        Assert.True(options.ShowPassRate);
        Assert.True(options.ShowDuration);
        Assert.False(options.ShowTokens);
        Assert.False(options.ShowCost);
        Assert.False(options.ShowToolCalls);
    }
    
    [Fact]
    public void OutputOptions_Full_ShowsAllColumns()
    {
        var options = OutputOptions.Full;
        
        Assert.True(options.ShowScore);
        Assert.True(options.ShowPassRate);
        Assert.True(options.ShowDuration);
        Assert.True(options.ShowTokens);
        Assert.True(options.ShowPromptTokens);
        Assert.True(options.ShowCompletionTokens);
        Assert.True(options.ShowCost);
        Assert.True(options.ShowToolCalls);
        Assert.True(options.ShowConfidenceInterval);
    }
    
    [Fact]
    public void PrintTable_HidesColumnsBasedOnOptions()
    {
        var result = CreateTestResult();
        using var sw = new StringWriter();
        var options = OutputOptions.Minimal.With(o => o.Writer = sw);
        
        TableFormatter.PrintTable(result, null, options);
        
        var output = sw.ToString();
        Assert.Contains("Score", output);
        Assert.Contains("Duration", output);
        Assert.DoesNotContain("Cost", output);
    }
    
    [Fact]
    public void PrintComparisonTable_ComparesMultipleModels()
    {
        var results = new List<(string ModelName, StochasticResult Result)>
        {
            ("GPT-4o", CreateTestResult(passRate: 0.9, meanScore: 85)),
            ("GPT-3.5", CreateTestResult(passRate: 0.7, meanScore: 70))
        };
        
        using var sw = new StringWriter();
        var options = new OutputOptions { Writer = sw };
        
        TableFormatter.PrintComparisonTable(results, options);
        
        var output = sw.ToString();
        Assert.Contains("GPT-4o", output);
        Assert.Contains("GPT-3.5", output);
        Assert.Contains("Model Comparison", output);
    }
    
    [Fact]
    public void Extension_PrintTable_ReturnsSameResult()
    {
        var result = CreateTestResult();
        using var sw = new StringWriter();
        var options = new OutputOptions { Writer = sw };
        
        var returned = result.PrintTable("Test", options);
        
        Assert.Same(result, returned);
    }
    
    [Fact]
    public void Extension_PrintComparisonTable_ReturnsSameList()
    {
        var results = new List<(string ModelName, StochasticResult Result)>
        {
            ("Model1", CreateTestResult())
        };
        
        using var sw = new StringWriter();
        var options = new OutputOptions { Writer = sw };
        
        var returned = results.PrintComparisonTable(options);
        
        Assert.Same(results, returned);
    }
    
    [Fact]
    public void OutputOptions_With_CreatesModifiedCopy()
    {
        var original = OutputOptions.Default;
        
        var modified = original.With(o => o.ShowCost = false);
        
        Assert.True(original.ShowCost);
        Assert.False(modified.ShowCost);
    }
    
    [Fact]
    public void PrintToolSummary_DisplaysToolInfo()
    {
        var summary = new ToolUsageSummary(
            ToolName: "CalculatorTool",
            RunsWithTool: 8,
            RunsWithErrors: 1,
            TotalCalls: 10,
            CallCountStats: new DistributionStatistics(1, 2, 1.25, 1, 1, 1.5, 2, 10),
            CallRate: 0.8,
            ErrorRate: 0.1);
        
        using var sw = new StringWriter();
        var options = new OutputOptions { Writer = sw };
        
        TableFormatter.PrintToolSummary(summary, options);
        
        var output = sw.ToString();
        Assert.Contains("CalculatorTool", output);
        Assert.Contains("Call Rate", output);
        Assert.Contains("80%", output);
    }
    
    [Fact]
    public void PrintToolSummary_ShowsNAWhenNoToolsCalled()
    {
        var summary = new ToolUsageSummary(
            ToolName: "CalculatorTool",
            RunsWithTool: 0,
            RunsWithErrors: 0,
            TotalCalls: 0,
            CallCountStats: new DistributionStatistics(0, 0, 0, 0, 0, 0, 0, 10),
            CallRate: 0.0,
            ErrorRate: 0.0);
        
        using var sw = new StringWriter();
        var options = new OutputOptions { Writer = sw };
        
        TableFormatter.PrintToolSummary(summary, options);
        
        var output = sw.ToString();
        Assert.Contains("CalculatorTool", output);
        Assert.Contains("N/A (No calls made)", output);
        Assert.DoesNotContain("100% Success", output);
    }
    
    [Fact]
    public void PrintPerformanceSummary_ShowsFastestSlowest()
    {
        var result = CreateTestResult();
        using var sw = new StringWriter();
        var options = new OutputOptions { Writer = sw };
        
        TableFormatter.PrintPerformanceSummary(result, options);
        
        var output = sw.ToString();
        Assert.Contains("Performance", output);
        Assert.Contains("Fastest", output);
        Assert.Contains("Slowest", output);
    }
}
