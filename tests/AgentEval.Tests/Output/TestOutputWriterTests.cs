// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Models;
using AgentEval.Output;

namespace AgentEval.Tests.Output;

public class TestOutputWriterTests
{
    [Fact]
    public void Constructor_WithDefaultSettings_DoesNotThrow()
    {
        var writer = new EvaluationOutputWriter();
        Assert.NotNull(writer);
    }

    [Fact]
    public void Constructor_WithSettings_DoesNotThrow()
    {
        var settings = new VerbositySettings { Level = VerbosityLevel.Full };
        var writer = new EvaluationOutputWriter(settings);
        Assert.NotNull(writer);
    }

    [Fact]
    public void Constructor_WithNullSettings_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new EvaluationOutputWriter(null!, Console.Out));
    }

    [Fact]
    public void Constructor_WithNullOutput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new EvaluationOutputWriter(new VerbositySettings(), null!));
    }

    [Fact]
    public void WriteTestResult_NullResult_Throws()
    {
        var writer = new EvaluationOutputWriter();
        Assert.Throws<ArgumentNullException>(() => writer.WriteTestResult(null!));
    }

    [Fact]
    public void WriteTestResult_None_OutputsPassFail()
    {
        var output = new StringWriter();
        var settings = new VerbositySettings { Level = VerbosityLevel.None };
        var writer = new EvaluationOutputWriter(settings, output);
        var result = CreatePassingResult();

        writer.WriteTestResult(result);

        var text = output.ToString();
        Assert.Contains("✓ PASS", text);
        Assert.Contains("TestPassed", text);
    }

    [Fact]
    public void WriteTestResult_None_FailedTest_OutputsFail()
    {
        var output = new StringWriter();
        var settings = new VerbositySettings { Level = VerbosityLevel.None };
        var writer = new EvaluationOutputWriter(settings, output);
        var result = CreateFailingResult();

        writer.WriteTestResult(result);

        var text = output.ToString();
        Assert.Contains("✗ FAIL", text);
        Assert.Contains("TestFailed", text);
    }

    [Fact]
    public void WriteTestResult_Summary_IncludesDuration()
    {
        var output = new StringWriter();
        var settings = new VerbositySettings { Level = VerbosityLevel.Summary };
        var writer = new EvaluationOutputWriter(settings, output);
        var result = CreatePassingResult();

        writer.WriteTestResult(result);

        var text = output.ToString();
        Assert.Contains("Duration:", text);
    }

    [Fact]
    public void WriteTestResult_Summary_IncludesToolCalls()
    {
        var output = new StringWriter();
        var settings = new VerbositySettings { Level = VerbosityLevel.Summary };
        var writer = new EvaluationOutputWriter(settings, output);
        var result = CreateResultWithTools();

        writer.WriteTestResult(result);

        var text = output.ToString();
        Assert.Contains("Tool calls:", text);
    }

    [Fact]
    public void WriteTestResult_Detailed_IncludesPerformanceSection()
    {
        var output = new StringWriter();
        var settings = new VerbositySettings { Level = VerbosityLevel.Detailed };
        var writer = new EvaluationOutputWriter(settings, output);
        var result = CreateResultWithPerformance();

        writer.WriteTestResult(result);

        var text = output.ToString();
        Assert.Contains("Performance:", text);
        Assert.Contains("Total Duration:", text);
    }

    [Fact]
    public void WriteTestResult_Detailed_IncludesToolsSection()
    {
        var output = new StringWriter();
        var settings = new VerbositySettings { Level = VerbosityLevel.Detailed };
        var writer = new EvaluationOutputWriter(settings, output);
        var result = CreateResultWithTools();

        writer.WriteTestResult(result);

        var text = output.ToString();
        Assert.Contains("Tools (", text);
        Assert.Contains("GetWeather", text);
    }

    [Fact]
    public void WriteTestResult_Detailed_IncludesMetrics()
    {
        var output = new StringWriter();
        var settings = new VerbositySettings { Level = VerbosityLevel.Detailed };
        var writer = new EvaluationOutputWriter(settings, output);
        var result = CreateResultWithMetrics();

        writer.WriteTestResult(result);

        var text = output.ToString();
        Assert.Contains("Metrics:", text);
        Assert.Contains("code_relevance", text);
    }

    [Fact]
    public void WriteTestResult_Detailed_IncludesError()
    {
        var output = new StringWriter();
        var settings = new VerbositySettings { Level = VerbosityLevel.Detailed };
        var writer = new EvaluationOutputWriter(settings, output);
        var result = CreateResultWithError();

        writer.WriteTestResult(result);

        var text = output.ToString();
        Assert.Contains("Error:", text);
        Assert.Contains("Test error", text);
    }

    [Fact]
    public void WriteTestResult_Full_IncludesJsonTrace()
    {
        var output = new StringWriter();
        var settings = new VerbositySettings { Level = VerbosityLevel.Full };
        var writer = new EvaluationOutputWriter(settings, output);
        var result = CreatePassingResult();

        writer.WriteTestResult(result);

        var text = output.ToString();
        Assert.Contains("Full JSON Trace:", text);
        Assert.Contains("```json", text);
        Assert.Contains("\"testName\":", text);
        Assert.Contains("\"passed\":", text);
    }

    [Fact]
    public void WriteTestResult_Detailed_HidesToolArgsWhenDisabled()
    {
        var output = new StringWriter();
        var settings = new VerbositySettings 
        { 
            Level = VerbosityLevel.Detailed,
            IncludeToolArguments = false
        };
        var writer = new EvaluationOutputWriter(settings, output);
        var result = CreateResultWithTools();

        writer.WriteTestResult(result);

        var text = output.ToString();
        Assert.Contains("GetWeather", text);
        Assert.DoesNotContain("Args:", text);
    }

    [Fact]
    public void WriteTestResult_Detailed_HidesToolResultsWhenDisabled()
    {
        var output = new StringWriter();
        var settings = new VerbositySettings 
        { 
            Level = VerbosityLevel.Detailed,
            IncludeToolResults = false
        };
        var writer = new EvaluationOutputWriter(settings, output);
        var result = CreateResultWithTools();

        writer.WriteTestResult(result);

        var text = output.ToString();
        Assert.Contains("GetWeather", text);
        Assert.DoesNotContain("Result:", text);
    }

    [Fact]
    public void WriteTestResult_Detailed_HidesPerformanceWhenDisabled()
    {
        var output = new StringWriter();
        var settings = new VerbositySettings 
        { 
            Level = VerbosityLevel.Detailed,
            IncludePerformanceMetrics = false
        };
        var writer = new EvaluationOutputWriter(settings, output);
        var result = CreateResultWithPerformance();

        writer.WriteTestResult(result);

        var text = output.ToString();
        Assert.DoesNotContain("Performance:", text);
    }

    #region Test Helpers

    private static TestResult CreatePassingResult()
    {
        return new TestResult
        {
            TestName = "TestPassed",
            Passed = true,
            Score = 100
        };
    }

    private static TestResult CreateFailingResult()
    {
        return new TestResult
        {
            TestName = "TestFailed",
            Passed = false,
            Score = 0
        };
    }

    private static TestResult CreateResultWithTools()
    {
        var result = new TestResult
        {
            TestName = "TestWithTools",
            Passed = true,
            Score = 100,
            ToolUsage = new ToolUsageReport()
        };
        
        result.ToolUsage.AddCall(new ToolCallRecord
        {
            Name = "GetWeather",
            CallId = "call_1",
            Order = 1,
            Arguments = new Dictionary<string, object?> { ["city"] = "Seattle" },
            Result = "72°F, Sunny",
            StartTime = DateTimeOffset.UtcNow,
            EndTime = DateTimeOffset.UtcNow.AddMilliseconds(100)
        });

        return result;
    }

    private static TestResult CreateResultWithPerformance()
    {
        return new TestResult
        {
            TestName = "TestWithPerformance",
            Passed = true,
            Score = 100,
            Performance = new PerformanceMetrics
            {
                StartTime = DateTimeOffset.UtcNow.AddSeconds(-1),
                EndTime = DateTimeOffset.UtcNow,
                PromptTokens = 100,
                CompletionTokens = 50,
                EstimatedCost = 0.005m,
                TimeToFirstToken = TimeSpan.FromMilliseconds(200)
            }
        };
    }

    private static TestResult CreateResultWithMetrics()
    {
        return new TestResult
        {
            TestName = "TestWithMetrics",
            Passed = true,
            Score = 95,
            MetricResults = new List<MetricResult>
            {
                new()
                {
                    MetricName = "code_relevance",
                    Score = 0.95,
                    Passed = true,
                    Explanation = "The response is highly relevant"
                }
            }
        };
    }

    private static TestResult CreateResultWithError()
    {
        return new TestResult
        {
            TestName = "TestWithError",
            Passed = false,
            Score = 0,
            Error = new InvalidOperationException("Test error")
        };
    }

    #endregion
}

/// <summary>
/// Additional edge case tests for EvaluationOutputWriter.
/// </summary>
public class TestOutputWriterEdgeCaseTests
{
    [Fact]
    public void WriteTestResult_WithFailure_IncludesSuggestions()
    {
        var output = new StringWriter();
        var settings = new VerbositySettings { Level = VerbosityLevel.Detailed };
        var writer = new EvaluationOutputWriter(settings, output);
        
        var failure = new FailureReport
        {
            WhyItFailed = "Expected tool was not called"
        };
        failure.AddReason("ToolNotCalled", "Tool 'GetWeather' was not invoked");
        failure.AddSuggestion("Add GetWeather to the tool list", "Ensure the agent has access to GetWeather");
        failure.AddSuggestion("Check the system prompt", "Verify the prompt mentions weather queries");
        
        var result = new TestResult
        {
            TestName = "TestWithFailure",
            Passed = false,
            Score = 0,
            Failure = failure
        };
        
        writer.WriteTestResult(result);
        var text = output.ToString();
        
        Assert.Contains("Expected tool was not called", text);
        Assert.Contains("Add GetWeather to the tool list", text);
    }

    [Fact]
    public void WriteTestResult_WithEmptyToolUsage_ShowsNoCalls()
    {
        var output = new StringWriter();
        var settings = new VerbositySettings { Level = VerbosityLevel.Detailed };
        var writer = new EvaluationOutputWriter(settings, output);
        
        var result = new TestResult
        {
            TestName = "TestEmptyTools",
            Passed = true,
            Score = 100,
            ToolUsage = new ToolUsageReport()  // Empty
        };
        
        writer.WriteTestResult(result);
        var text = output.ToString();
        
        Assert.Contains("no tool calls recorded", text);
    }

    [Fact]
    public void WriteTestResult_WithToolError_ShowsErrorMessage()
    {
        var output = new StringWriter();
        var settings = new VerbositySettings { Level = VerbosityLevel.Detailed };
        var writer = new EvaluationOutputWriter(settings, output);
        
        var result = new TestResult
        {
            TestName = "TestToolError",
            Passed = false,
            Score = 0,
            ToolUsage = new ToolUsageReport()
        };
        
        result.ToolUsage.AddCall(new ToolCallRecord
        {
            Name = "FailingTool",
            CallId = "call_1",
            Order = 1,
            Arguments = new Dictionary<string, object?> { ["input"] = "test" },
            Exception = new InvalidOperationException("Tool failed"),
            StartTime = DateTimeOffset.UtcNow,
            EndTime = DateTimeOffset.UtcNow.AddMilliseconds(100)
        });
        
        writer.WriteTestResult(result);
        var text = output.ToString();
        
        Assert.Contains("FailingTool", text);
        Assert.Contains("Tool failed", text);
    }

    [Fact]
    public void WriteTestResult_Summary_WithTokens_ShowsTokenCount()
    {
        var output = new StringWriter();
        var settings = new VerbositySettings { Level = VerbosityLevel.Summary };
        var writer = new EvaluationOutputWriter(settings, output);
        
        var result = new TestResult
        {
            TestName = "TestTokens",
            Passed = true,
            Score = 100,
            Performance = new PerformanceMetrics
            {
                StartTime = DateTimeOffset.UtcNow.AddSeconds(-1),
                EndTime = DateTimeOffset.UtcNow,
                PromptTokens = 500,
                CompletionTokens = 200
            }
        };
        
        writer.WriteTestResult(result);
        var text = output.ToString();
        
        Assert.Contains("Tokens: 700", text);  // Total tokens
    }

    [Fact]
    public void WriteTestResult_Detailed_WithEstimatedCost_ShowsCost()
    {
        var output = new StringWriter();
        var settings = new VerbositySettings { Level = VerbosityLevel.Detailed };
        var writer = new EvaluationOutputWriter(settings, output);
        
        var result = new TestResult
        {
            TestName = "TestCost",
            Passed = true,
            Score = 100,
            Performance = new PerformanceMetrics
            {
                StartTime = DateTimeOffset.UtcNow.AddSeconds(-1),
                EndTime = DateTimeOffset.UtcNow,
                EstimatedCost = 0.0125m
            }
        };
        
        writer.WriteTestResult(result);
        var text = output.ToString();
        
        Assert.Contains("$0.0125", text);
    }

    [Fact]
    public void WriteTestResult_Full_IncludesToolUsageInJson()
    {
        var output = new StringWriter();
        var settings = new VerbositySettings { Level = VerbosityLevel.Full };
        var writer = new EvaluationOutputWriter(settings, output);
        
        var result = new TestResult
        {
            TestName = "TestFullJson",
            Passed = true,
            Score = 100,
            ToolUsage = new ToolUsageReport()
        };
        
        result.ToolUsage.AddCall(new ToolCallRecord
        {
            Name = "JsonTool",
            CallId = "call_json",
            Order = 1,
            Arguments = new Dictionary<string, object?> { ["arg1"] = "value1" },
            Result = "Success",
            StartTime = DateTimeOffset.UtcNow,
            EndTime = DateTimeOffset.UtcNow.AddMilliseconds(50)
        });
        
        writer.WriteTestResult(result);
        var text = output.ToString();
        
        Assert.Contains("\"totalCalls\": 1", text);
        Assert.Contains("\"name\": \"JsonTool\"", text);
    }

    [Fact]
    public void WriteTestResult_VeryLongToolResult_Truncates()
    {
        var output = new StringWriter();
        var settings = new VerbositySettings 
        { 
            Level = VerbosityLevel.Detailed,
            IncludeToolResults = true
        };
        var writer = new EvaluationOutputWriter(settings, output);
        
        var longResult = new string('X', 500);  // 500 characters
        var result = new TestResult
        {
            TestName = "TestLongResult",
            Passed = true,
            Score = 100,
            ToolUsage = new ToolUsageReport()
        };
        
        result.ToolUsage.AddCall(new ToolCallRecord
        {
            Name = "LongResultTool",
            CallId = "call_1",
            Order = 1,
            Result = longResult,
            StartTime = DateTimeOffset.UtcNow,
            EndTime = DateTimeOffset.UtcNow.AddMilliseconds(50)
        });
        
        writer.WriteTestResult(result);
        var text = output.ToString();
        
        // Should be truncated with "..."
        Assert.Contains("...", text);
        // Should not contain the full 500-character string
        Assert.True(text.Length < longResult.Length + 200);
    }

    [Fact]
    public void WriteTestResult_NullToolResult_ShowsEmpty()
    {
        var output = new StringWriter();
        var settings = new VerbositySettings 
        { 
            Level = VerbosityLevel.Detailed,
            IncludeToolResults = true
        };
        var writer = new EvaluationOutputWriter(settings, output);
        
        var result = new TestResult
        {
            TestName = "TestNullResult",
            Passed = true,
            Score = 100,
            ToolUsage = new ToolUsageReport()
        };
        
        result.ToolUsage.AddCall(new ToolCallRecord
        {
            Name = "NullTool",
            CallId = "call_1",
            Order = 1,
            Result = null,
            StartTime = DateTimeOffset.UtcNow,
            EndTime = DateTimeOffset.UtcNow.AddMilliseconds(50)
        });
        
        writer.WriteTestResult(result);
        var text = output.ToString();
        
        Assert.Contains("NullTool", text);
        // Should not crash
    }

    [Fact]
    public void WriteTestResult_WithTimeToFirstToken_ShowsTTFT()
    {
        var output = new StringWriter();
        var settings = new VerbositySettings { Level = VerbosityLevel.Detailed };
        var writer = new EvaluationOutputWriter(settings, output);
        
        var result = new TestResult
        {
            TestName = "TestTTFT",
            Passed = true,
            Score = 100,
            Performance = new PerformanceMetrics
            {
                StartTime = DateTimeOffset.UtcNow.AddSeconds(-1),
                EndTime = DateTimeOffset.UtcNow,
                TimeToFirstToken = TimeSpan.FromMilliseconds(150)
            }
        };
        
        writer.WriteTestResult(result);
        var text = output.ToString();
        
        Assert.Contains("Time to First Token:", text);
        Assert.Contains("150ms", text);
    }

    [Fact]
    public void WriteTestResult_MultipleTools_SortedByOrder()
    {
        var output = new StringWriter();
        var settings = new VerbositySettings { Level = VerbosityLevel.Detailed };
        var writer = new EvaluationOutputWriter(settings, output);
        
        var result = new TestResult
        {
            TestName = "TestToolOrder",
            Passed = true,
            Score = 100,
            ToolUsage = new ToolUsageReport()
        };
        
        // Add in reverse order
        result.ToolUsage.AddCall(new ToolCallRecord
        {
            Name = "ThirdTool",
            CallId = "call_3",
            Order = 3,
            StartTime = DateTimeOffset.UtcNow.AddMilliseconds(200),
            EndTime = DateTimeOffset.UtcNow.AddMilliseconds(250)
        });
        result.ToolUsage.AddCall(new ToolCallRecord
        {
            Name = "FirstTool",
            CallId = "call_1",
            Order = 1,
            StartTime = DateTimeOffset.UtcNow,
            EndTime = DateTimeOffset.UtcNow.AddMilliseconds(50)
        });
        result.ToolUsage.AddCall(new ToolCallRecord
        {
            Name = "SecondTool",
            CallId = "call_2",
            Order = 2,
            StartTime = DateTimeOffset.UtcNow.AddMilliseconds(100),
            EndTime = DateTimeOffset.UtcNow.AddMilliseconds(150)
        });
        
        writer.WriteTestResult(result);
        var text = output.ToString();
        
        // Check order in output
        var firstPos = text.IndexOf("FirstTool");
        var secondPos = text.IndexOf("SecondTool");
        var thirdPos = text.IndexOf("ThirdTool");
        
        Assert.True(firstPos < secondPos);
        Assert.True(secondPos < thirdPos);
    }
}
