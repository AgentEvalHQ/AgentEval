// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Models;
using AgentEval.Output;

namespace AgentEval.Tests.Output;

// Concrete implementation for testing
internal class TestableAgentEvalTestBase : AgentEvalTestBase
{
    public TestableAgentEvalTestBase() : base()
    {
    }

    public TestableAgentEvalTestBase(TextWriter? output) : base(output)
    {
    }

    public TestableAgentEvalTestBase(TextWriter? output, VerbositySettings? settings) 
        : base(output, settings)
    {
    }

    // Expose protected methods for testing
    public void TestRecordResult(TestResult result) => RecordResult(result);
    public string TestSaveTrace(TimeTravelTrace trace) => SaveTrace(trace);
    public string GetTraceDirectoryForTest() => TraceDirectory;
    public void TestWriteLine(string message) => WriteLine(message);
    public void TestWriteLine(string format, params object[] args) => WriteLine(format, args);
    public void TestFlushOutput() => FlushOutput();
    public string TestGetCapturedOutput() => GetCapturedOutput();
    public TestResultBuilder TestCreateResult(string testName) => CreateResult(testName);
    public new bool SaveTracesForAllTests { get => base.SaveTracesForAllTests; set => base.SaveTracesForAllTests = value; }
}

public class AgentEvalTestBaseTests : IDisposable
{
    private readonly string _testDirectory;
    
    public AgentEvalTestBaseTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"AgentEval_TestBase_{Guid.NewGuid():N}");
    }
    
    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            try
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public void Constructor_Default_Works()
    {
        using var testBase = new TestableAgentEvalTestBase();
        Assert.NotNull(testBase);
    }

    [Fact]
    public void Constructor_WithTextWriter_Works()
    {
        var stringWriter = new StringWriter();
        using var testBase = new TestableAgentEvalTestBase(stringWriter);
        Assert.NotNull(testBase);
    }

    [Fact]
    public void Constructor_WithSettings_UsesCustomSettings()
    {
        var settings = new VerbositySettings 
        { 
            Level = VerbosityLevel.Full,
            TraceOutputDirectory = _testDirectory
        };
        
        using var testBase = new TestableAgentEvalTestBase(null, settings);
        
        Assert.Equal(_testDirectory, testBase.GetTraceDirectoryForTest());
    }

    [Fact]
    public void RecordResult_CapturesOutput()
    {
        // Provide a StringWriter to capture the flushed output
        var stringWriter = new StringWriter();
        var settings = new VerbositySettings 
        { 
            Level = VerbosityLevel.Summary,
            SaveTraceFiles = false
        };
        using var testBase = new TestableAgentEvalTestBase(stringWriter, settings);
        
        var result = new TestResult
        {
            TestName = "TestCapture",
            Passed = true,
            Score = 100
        };
        
        testBase.TestRecordResult(result);
        
        // RecordResult flushes to external output, so check the StringWriter
        var output = stringWriter.ToString();
        
        Assert.Contains("TestCapture", output);
        Assert.Contains("PASS", output);
    }

    [Fact]
    public void RecordResult_WritesToExternalOutput()
    {
        var stringWriter = new StringWriter();
        var settings = new VerbositySettings 
        { 
            Level = VerbosityLevel.None,
            SaveTraceFiles = false
        };
        using var testBase = new TestableAgentEvalTestBase(stringWriter, settings);
        
        var result = new TestResult
        {
            TestName = "EvaluationOutputWriter",
            Passed = true,
            Score = 100
        };
        
        testBase.TestRecordResult(result);
        
        var output = stringWriter.ToString();
        Assert.NotEmpty(output);
        Assert.Contains("EvaluationOutputWriter", output);
    }

    [Fact]
    public void RecordResult_SavesTraceOnFailure()
    {
        var settings = new VerbositySettings 
        { 
            Level = VerbosityLevel.None,
            SaveTraceFiles = true,
            TraceOutputDirectory = _testDirectory
        };
        using var testBase = new TestableAgentEvalTestBase(null, settings);
        
        var result = new TestResult
        {
            TestName = "FailedTest",
            Passed = false,
            Score = 0
        };
        
        testBase.TestRecordResult(result);
        
        var traceFiles = Directory.GetFiles(_testDirectory, "*.json");
        Assert.Single(traceFiles);
    }

    [Fact]
    public void RecordResult_SkipsTraceForPassingTest()
    {
        var settings = new VerbositySettings 
        { 
            Level = VerbosityLevel.None,
            SaveTraceFiles = true,
            TraceOutputDirectory = _testDirectory
        };
        using var testBase = new TestableAgentEvalTestBase(null, settings);
        
        var result = new TestResult
        {
            TestName = "PassingTest",
            Passed = true,
            Score = 100
        };
        
        testBase.TestRecordResult(result);
        
        if (Directory.Exists(_testDirectory))
        {
            var traceFiles = Directory.GetFiles(_testDirectory, "*.json");
            Assert.Empty(traceFiles);
        }
    }

    [Fact]
    public void RecordResult_SavesTraceForAllTests_WhenEnabled()
    {
        var settings = new VerbositySettings 
        { 
            Level = VerbosityLevel.None,
            SaveTraceFiles = true,
            TraceOutputDirectory = _testDirectory
        };
        using var testBase = new TestableAgentEvalTestBase(null, settings);
        testBase.SaveTracesForAllTests = true;
        
        var result = new TestResult
        {
            TestName = "PassingTestWithTrace",
            Passed = true,
            Score = 100
        };
        
        testBase.TestRecordResult(result);
        
        var traceFiles = Directory.GetFiles(_testDirectory, "*.json");
        Assert.Single(traceFiles);
    }

    [Fact]
    public void WriteLine_CapturesOutput()
    {
        using var testBase = new TestableAgentEvalTestBase();
        
        testBase.TestWriteLine("Custom message");
        testBase.TestWriteLine("Value: {0}", 42);
        
        var output = testBase.TestGetCapturedOutput();
        Assert.Contains("Custom message", output);
        Assert.Contains("Value: 42", output);
    }

    [Fact]
    public void CreateResult_ReturnsBuilder()
    {
        var settings = new VerbositySettings 
        { 
            Level = VerbosityLevel.None,
            SaveTraceFiles = false
        };
        using var testBase = new TestableAgentEvalTestBase(null, settings);
        
        var builder = testBase.TestCreateResult("BuilderTest");
        
        Assert.NotNull(builder);
    }

    [Fact]
    public void TestResultBuilder_Passed_SetsCorrectValues()
    {
        var settings = new VerbositySettings 
        { 
            Level = VerbosityLevel.None,
            SaveTraceFiles = false
        };
        using var testBase = new TestableAgentEvalTestBase(null, settings);
        
        var result = testBase.TestCreateResult("PassedBuilder")
            .Passed(95)
            .WithOutput("Test output")
            .Build();
        
        Assert.True(result.Passed);
        Assert.Equal(95, result.Score);
        Assert.Equal("Test output", result.ActualOutput);
    }

    [Fact]
    public void TestResultBuilder_Failed_SetsCorrectValues()
    {
        var settings = new VerbositySettings 
        { 
            Level = VerbosityLevel.None,
            SaveTraceFiles = false
        };
        using var testBase = new TestableAgentEvalTestBase(null, settings);
        
        var result = testBase.TestCreateResult("FailedBuilder")
            .Failed("Test failed because...")
            .Build();
        
        Assert.False(result.Passed);
        Assert.Equal(0, result.Score);
        Assert.Equal("Test failed because...", result.Details);
    }

    [Fact]
    public void TestResultBuilder_WithToolCall_AddsToolToReport()
    {
        var settings = new VerbositySettings 
        { 
            Level = VerbosityLevel.None,
            SaveTraceFiles = false
        };
        using var testBase = new TestableAgentEvalTestBase(null, settings);
        
        var result = testBase.TestCreateResult("ToolBuilder")
            .WithToolCall("GetWeather", "call_1", 
                new Dictionary<string, object?> { ["city"] = "Seattle" },
                "72°F, Sunny")
            .WithToolCall("SendEmail", "call_2")
            .Passed()
            .Build();
        
        Assert.Equal(2, result.ToolUsage!.Count);
        Assert.True(result.ToolUsage.WasToolCalled("GetWeather"));
        Assert.True(result.ToolUsage.WasToolCalled("SendEmail"));
    }

    [Fact]
    public void TestResultBuilder_WithTokens_SetsPerformance()
    {
        var settings = new VerbositySettings 
        { 
            Level = VerbosityLevel.None,
            SaveTraceFiles = false
        };
        using var testBase = new TestableAgentEvalTestBase(null, settings);
        
        var result = testBase.TestCreateResult("TokenBuilder")
            .WithTokens(100, 50)
            .WithCost(0.005m)
            .Passed()
            .Build();
        
        Assert.Equal(100, result.Performance!.PromptTokens);
        Assert.Equal(50, result.Performance.CompletionTokens);
        Assert.Equal(0.005m, result.Performance.EstimatedCost);
    }

    [Fact]
    public void TestResultBuilder_WithError_MarksFailed()
    {
        var settings = new VerbositySettings 
        { 
            Level = VerbosityLevel.None,
            SaveTraceFiles = false
        };
        using var testBase = new TestableAgentEvalTestBase(null, settings);
        
        var error = new InvalidOperationException("Something went wrong");
        var result = testBase.TestCreateResult("ErrorBuilder")
            .WithError(error)
            .Build();
        
        Assert.False(result.Passed);
        Assert.Equal(error, result.Error);
    }

    [Fact]
    public void SaveTrace_CreatesFile()
    {
        var settings = new VerbositySettings 
        { 
            TraceOutputDirectory = _testDirectory
        };
        using var testBase = new TestableAgentEvalTestBase(null, settings);
        
        var trace = CreateSampleTrace();
        var filePath = testBase.TestSaveTrace(trace);
        
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public void Dispose_FlushesOutput()
    {
        var stringWriter = new StringWriter();
        var settings = new VerbositySettings 
        { 
            Level = VerbosityLevel.None,
            SaveTraceFiles = false
        };
        
        using (var testBase = new TestableAgentEvalTestBase(stringWriter, settings))
        {
            testBase.TestWriteLine("Before dispose");
        }
        
        // Output should be flushed after dispose
        Assert.Contains("Before dispose", stringWriter.ToString());
    }

    #region Test Helpers

    private static TimeTravelTrace CreateSampleTrace()
    {
        var startTime = DateTimeOffset.UtcNow.AddSeconds(-1);
        var endTime = DateTimeOffset.UtcNow;

        return new TimeTravelTrace
        {
            TraceId = "trace_" + Guid.NewGuid().ToString("N")[..8],
            ExecutionType = ExecutionType.SingleAgent,
            Test = new EvaluationMetadata
            {
                TestName = "SampleTest",
                StartTime = startTime,
                EndTime = endTime,
                Passed = true
            },
            Agents = new List<AgentEval.Output.AgentInfo>
            {
                new()
                {
                    AgentId = "agent_1",
                    AgentName = "TestAgent",
                    ModelId = "gpt-4o"
                }
            },
            Steps = new List<ExecutionStep>(),
            Summary = new ExecutionSummary
            {
                Passed = true,
                TotalDuration = endTime - startTime,
                TotalSteps = 0,
                ToolCallCount = 0,
                ToolErrorCount = 0,
                LlmRequestCount = 1
            }
        };
    }

    #endregion
}
