// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Core;
using AgentEval.Models;
using AgentEval.Output;

namespace AgentEval.Tests.Output;

public class TraceArtifactManagerTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly TraceArtifactManager _manager;

    public TraceArtifactManagerTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"AgentEval_Tests_{Guid.NewGuid():N}");
        _manager = new TraceArtifactManager(_testDirectory);
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
    public void Constructor_Default_UsesConfigurationDirectory()
    {
        var manager = new TraceArtifactManager();
        Assert.Equal(VerbosityConfiguration.TraceDirectory, manager.OutputDirectory);
    }

    [Fact]
    public void Constructor_WithCustomDirectory_UsesCustomDirectory()
    {
        Assert.Equal(_testDirectory, _manager.OutputDirectory);
    }

    [Fact]
    public void Constructor_NullDirectory_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new TraceArtifactManager(null!));
    }

    [Fact]
    public void SaveTestResult_CreatesDirectoryIfNeeded()
    {
        var result = CreatePassingResult();

        var filePath = _manager.SaveTestResult(result);

        Assert.True(Directory.Exists(_testDirectory));
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public void SaveTestResult_CreatesJsonFile()
    {
        var result = CreatePassingResult();

        var filePath = _manager.SaveTestResult(result);

        Assert.EndsWith(".json", filePath);
        var content = File.ReadAllText(filePath);
        Assert.Contains("\"testName\":", content);
        Assert.Contains("\"passed\":", content);
    }

    [Fact]
    public void SaveTestResult_IncludesTimestampInFileName()
    {
        var result = CreatePassingResult();

        var filePath = _manager.SaveTestResult(result);
        var fileName = Path.GetFileName(filePath);

        // Filename should contain date/time pattern
        Assert.Matches(@"\d{8}_\d{6}_\d{3}", fileName);
    }

    [Fact]
    public void SaveTestResult_SanitizesTestName()
    {
        var result = new TestResult
        {
            TestName = "Test/With:Invalid*Characters?",
            Passed = true,
            Score = 100
        };

        var filePath = _manager.SaveTestResult(result);
        var fileName = Path.GetFileName(filePath);

        Assert.DoesNotContain("/", fileName);
        Assert.DoesNotContain(":", fileName);
        Assert.DoesNotContain("*", fileName);
        Assert.DoesNotContain("?", fileName);
    }

    [Fact]
    public void SaveIfFailed_ReturnsNullForPassingTest()
    {
        var result = CreatePassingResult();

        var filePath = _manager.SaveIfFailed(result);

        Assert.Null(filePath);
    }

    [Fact]
    public void SaveIfFailed_SavesFailingTest()
    {
        var result = CreateFailingResult();

        var filePath = _manager.SaveIfFailed(result);

        Assert.NotNull(filePath);
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public void SaveTrace_CreatesFile()
    {
        var trace = CreateSampleTrace();

        var filePath = _manager.SaveTrace(trace);

        Assert.True(File.Exists(filePath));
        var content = File.ReadAllText(filePath);
        Assert.Contains("\"traceId\":", content);
        Assert.Contains("\"executionType\":", content);
    }

    [Fact]
    public void LoadTrace_ReturnsDeserializedTrace()
    {
        var original = CreateSampleTrace();
        var filePath = _manager.SaveTrace(original);

        var loaded = _manager.LoadTrace(filePath);

        Assert.NotNull(loaded);
        Assert.Equal(original.TraceId, loaded.TraceId);
        Assert.Equal(original.ExecutionType, loaded.ExecutionType);
    }

    [Fact]
    public void LoadTrace_ThrowsForMissingFile()
    {
        var nonExistentPath = Path.Combine(_testDirectory, "does_not_exist.json");

        Assert.Throws<FileNotFoundException>(() => _manager.LoadTrace(nonExistentPath));
    }

    [Fact]
    public void ListTraceFiles_ReturnsFilesInOrder()
    {
        // Create multiple trace files
        _manager.SaveTestResult(new TestResult { TestName = "Test1", Passed = true, Score = 100 });
        Thread.Sleep(10); // Ensure different timestamps
        _manager.SaveTestResult(new TestResult { TestName = "Test2", Passed = true, Score = 100 });
        Thread.Sleep(10);
        _manager.SaveTestResult(new TestResult { TestName = "Test3", Passed = true, Score = 100 });

        var files = _manager.ListTraceFiles().ToList();

        Assert.Equal(3, files.Count);
        // Most recent first
        Assert.Contains("Test3", files[0]);
        Assert.Contains("Test1", files[2]);
    }

    [Fact]
    public void ListTraceFiles_ReturnsEmptyForMissingDirectory()
    {
        var nonExistentDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var manager = new TraceArtifactManager(nonExistentDir);

        var files = manager.ListTraceFiles().ToList();

        Assert.Empty(files);
    }

    [Fact]
    public void CleanupOldTraces_DeletesOldFiles()
    {
        // Create a file and set its creation time to old
        var result = CreatePassingResult();
        var filePath = _manager.SaveTestResult(result);
        File.SetCreationTimeUtc(filePath, DateTime.UtcNow.AddDays(-7));

        var deleted = _manager.CleanupOldTraces(TimeSpan.FromDays(1));

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void CleanupOldTraces_KeepsRecentFiles()
    {
        var result = CreatePassingResult();
        var filePath = _manager.SaveTestResult(result);

        var deleted = _manager.CleanupOldTraces(TimeSpan.FromDays(1));

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public void GetMostRecentTrace_ReturnsLatestFile()
    {
        var testName = "RecentTraceTest";
        _manager.SaveTestResult(new TestResult { TestName = testName, Passed = true, Score = 100 });
        Thread.Sleep(10);
        _manager.SaveTestResult(new TestResult { TestName = testName, Passed = false, Score = 50 });

        var mostRecent = _manager.GetMostRecentTrace(testName);

        Assert.NotNull(mostRecent);
        // The most recent file should be the second one (failed test)
        var content = File.ReadAllText(mostRecent);
        Assert.Contains("\"passed\": false", content);
    }

    [Fact]
    public void GetMostRecentTrace_ReturnsNullWhenNoFilesMatch()
    {
        var mostRecent = _manager.GetMostRecentTrace("NonExistentTest");

        Assert.Null(mostRecent);
    }

    [Fact]
    public void SaveRawData_SavesArbitraryObject()
    {
        var data = new { Name = "Test", Value = 42, Items = new[] { "A", "B", "C" } };

        var filePath = _manager.SaveRawData("custom_data", data);

        Assert.True(File.Exists(filePath));
        var content = File.ReadAllText(filePath);
        Assert.Contains("\"name\": \"Test\"", content);
        Assert.Contains("\"value\": 42", content);
    }

    [Fact]
    public void SaveTestResult_IncludesToolUsage()
    {
        var result = CreateResultWithTools();

        var filePath = _manager.SaveTestResult(result);
        var content = File.ReadAllText(filePath);

        Assert.Contains("\"toolUsage\":", content);
        Assert.Contains("GetWeather", content);
    }

    [Fact]
    public void SaveTestResult_IncludesPerformanceMetrics()
    {
        var result = CreateResultWithPerformance();

        var filePath = _manager.SaveTestResult(result);
        var content = File.ReadAllText(filePath);

        Assert.Contains("\"performance\":", content);
        Assert.Contains("\"totalDurationMs\":", content);
    }

    [Fact]
    public void SaveTestResult_IncludesMetricResults()
    {
        var result = CreateResultWithMetrics();

        var filePath = _manager.SaveTestResult(result);
        var content = File.ReadAllText(filePath);

        Assert.Contains("\"metrics\":", content);
        Assert.Contains("code_relevance", content);
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
            Score = 0,
            Error = new InvalidOperationException("Test failed")
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
                EstimatedCost = 0.005m
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

    private static TimeTravelTrace CreateSampleTrace()
    {
        var startTime = DateTimeOffset.UtcNow.AddSeconds(-2);
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
            Agents = new List<AgentInfo>
            {
                new()
                {
                    AgentId = "agent_1",
                    AgentName = "TestAgent",
                    ModelId = "gpt-4o"
                }
            },
            Steps = new List<ExecutionStep>
            {
                new()
                {
                    StepNumber = 1,
                    Type = StepType.UserInput,
                    Timestamp = startTime,
                    OffsetFromStart = TimeSpan.Zero,
                    Duration = TimeSpan.FromMilliseconds(10),
                    Data = new UserInputStepData { Message = "Hello" }
                }
            },
            Summary = new ExecutionSummary
            {
                Passed = true,
                TotalDuration = endTime - startTime,
                TotalSteps = 1,
                ToolCallCount = 0,
                ToolErrorCount = 0,
                LlmRequestCount = 1
            }
        };
    }

    #endregion
}
