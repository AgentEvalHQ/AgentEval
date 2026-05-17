// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Models;
using AgentEval.Testing;
using AgentEval.Tests.TestHelpers;
using Xunit;
using AgenticBenchmark = AgentEval.Benchmarks.AgenticBenchmark;

namespace AgentEval.Tests.Benchmarks;

/// <summary>
/// Tests for AgenticBenchmark.
/// </summary>
public class AgenticBenchmarkTests
{
    #region Tool Accuracy Benchmark Tests

    [Fact]
    public async Task RunToolAccuracyBenchmarkAsync_AllToolsCorrect_Returns100Accuracy()
    {
        // Arrange
        var mockAgent = new MockTestableAgent(
            name: "TestAgent",
            responseText: "Weather checked!",
            toolCalls: [("GetWeather", new Dictionary<string, object?> { ["city"] = "Paris" })]
        );
        var benchmark = new AgenticBenchmark(mockAgent, options: new AgenticBenchmarkOptions { Verbose = false });
        
        var testCases = new[]
        {
            new ToolAccuracyTestCase
            {
                Name = "Weather Check",
                Prompt = "What's the weather in Paris?",
                ExpectedTools = new[] { new ExpectedTool { Name = "GetWeather", RequiredParameters = new[] { "city" } } }
            }
        };
        
        // Act
        var result = await benchmark.RunToolAccuracyBenchmarkAsync(testCases);
        
        // Assert
        Assert.Equal("TestAgent", result.AgentName);
        Assert.Equal(1, result.TotalTests);
        Assert.Equal(1, result.PassedTests);
        Assert.Equal(1.0, result.OverallAccuracy);
    }
    
    [Fact]
    public async Task RunToolAccuracyBenchmarkAsync_MissingTool_Returns0Accuracy()
    {
        // Arrange
        var mockAgent = new MockTestableAgent(
            name: "TestAgent",
            responseText: "Done!",
            toolCalls: [] // No tools called
        );
        var benchmark = new AgenticBenchmark(mockAgent, options: new AgenticBenchmarkOptions { Verbose = false });
        
        var testCases = new[]
        {
            new ToolAccuracyTestCase
            {
                Name = "Tool Required",
                Prompt = "Use the tool",
                ExpectedTools = new[] { new ExpectedTool { Name = "RequiredTool" } }
            }
        };
        
        // Act
        var result = await benchmark.RunToolAccuracyBenchmarkAsync(testCases);
        
        // Assert
        Assert.Equal(0, result.PassedTests);
        Assert.Single(result.Results);
        Assert.False(result.Results[0].Passed);
        Assert.Contains("RequiredTool", result.Results[0].ToolsMissed);
    }
    
    [Fact]
    public async Task RunToolAccuracyBenchmarkAsync_UnexpectedTool_Fails()
    {
        // Arrange
        var mockAgent = new MockTestableAgent(
            name: "TestAgent",
            responseText: "Done!",
            toolCalls: [("ExpectedTool", new Dictionary<string, object?>()), ("UnexpectedTool", new Dictionary<string, object?>())]
        );
        var benchmark = new AgenticBenchmark(mockAgent, options: new AgenticBenchmarkOptions { Verbose = false });
        
        var testCases = new[]
        {
            new ToolAccuracyTestCase
            {
                Name = "Strict Tools",
                Prompt = "Use the tool",
                ExpectedTools = new[] { new ExpectedTool { Name = "ExpectedTool" } },
                AllowExtraTools = false
            }
        };
        
        // Act
        var result = await benchmark.RunToolAccuracyBenchmarkAsync(testCases);
        
        // Assert
        Assert.Single(result.Results);
        Assert.False(result.Results[0].Passed);
        Assert.Contains("UnexpectedTool", result.Results[0].UnexpectedTools);
    }
    
    [Fact]
    public async Task RunToolAccuracyBenchmarkAsync_MissingParameter_ReportsError()
    {
        // Arrange
        var mockAgent = new MockTestableAgent(
            name: "TestAgent",
            responseText: "Done!",
            toolCalls: [("SendEmail", new Dictionary<string, object?> { ["to"] = "test@test.com" })] // Missing subject
        );
        var benchmark = new AgenticBenchmark(mockAgent, options: new AgenticBenchmarkOptions { Verbose = false });
        
        var testCases = new[]
        {
            new ToolAccuracyTestCase
            {
                Name = "Email Test",
                Prompt = "Send an email",
                ExpectedTools = new[] { new ExpectedTool { Name = "SendEmail", RequiredParameters = new[] { "to", "subject" } } }
            }
        };
        
        // Act
        var result = await benchmark.RunToolAccuracyBenchmarkAsync(testCases);
        
        // Assert
        Assert.Single(result.Results);
        Assert.False(result.Results[0].Passed);
        Assert.Contains(result.Results[0].ParameterErrors, e => e.Contains("subject"));
    }
    
    [Fact]
    public async Task RunToolAccuracyBenchmarkAsync_MultipleTestCases_AggregatesResults()
    {
        // Arrange
        var mockAgent = new MockTestableAgent(
            name: "TestAgent",
            responseText: "Done!",
            toolCalls: [("ToolA", new Dictionary<string, object?>())]
        );
        var benchmark = new AgenticBenchmark(mockAgent, options: new AgenticBenchmarkOptions { Verbose = false });
        
        var testCases = new[]
        {
            new ToolAccuracyTestCase
            {
                Name = "Test 1",
                Prompt = "Use ToolA",
                ExpectedTools = new[] { new ExpectedTool { Name = "ToolA" } }
            },
            new ToolAccuracyTestCase
            {
                Name = "Test 2",
                Prompt = "Use ToolB",
                ExpectedTools = new[] { new ExpectedTool { Name = "ToolB" } } // Will fail
            }
        };
        
        // Act
        var result = await benchmark.RunToolAccuracyBenchmarkAsync(testCases);
        
        // Assert
        Assert.Equal(2, result.TotalTests);
        Assert.Equal(1, result.PassedTests);
        Assert.Equal(0.5, result.OverallAccuracy);
    }

    #endregion

    #region Multi-Step Reasoning Benchmark Tests

    [Fact]
    public async Task RunMultiStepReasoningBenchmarkAsync_AllStepsCompleted_Passes()
    {
        // Arrange
        var mockAgent = new MockTestableAgent(
            name: "TestAgent",
            responseText: "Done!",
            toolCalls:
            [
                ("Step1Tool", new Dictionary<string, object?>()),
                ("Step2Tool", new Dictionary<string, object?>()),
                ("Step3Tool", new Dictionary<string, object?>())
            ]
        );
        var benchmark = new AgenticBenchmark(mockAgent, options: new AgenticBenchmarkOptions { Verbose = false });
        
        var testCases = new[]
        {
            new MultiStepTestCase
            {
                Name = "Three Step Process",
                Prompt = "Execute workflow",
                ExpectedSteps = new[]
                {
                    new ExpectedStep { ToolName = "Step1Tool" },
                    new ExpectedStep { ToolName = "Step2Tool" },
                    new ExpectedStep { ToolName = "Step3Tool" }
                }
            }
        };
        
        // Act
        var result = await benchmark.RunMultiStepReasoningBenchmarkAsync(testCases);
        
        // Assert
        Assert.Equal(1, result.TotalTests);
        Assert.Equal(1, result.PassedTests);
        Assert.Single(result.Results);
        Assert.True(result.Results[0].Passed);
        Assert.Equal(3, result.Results[0].CompletedSteps);
    }
    
    [Fact]
    public async Task RunMultiStepReasoningBenchmarkAsync_PartialCompletion_ReportsCorrectly()
    {
        // Arrange
        var mockAgent = new MockTestableAgent(
            name: "TestAgent",
            responseText: "Partial!",
            toolCalls:
            [
                ("Step1Tool", new Dictionary<string, object?>()),
                ("Step2Tool", new Dictionary<string, object?>())
                // Missing Step3Tool
            ]
        );
        var benchmark = new AgenticBenchmark(mockAgent, options: new AgenticBenchmarkOptions { Verbose = false });
        
        var testCases = new[]
        {
            new MultiStepTestCase
            {
                Name = "Incomplete Workflow",
                Prompt = "Execute workflow",
                ExpectedSteps = new[]
                {
                    new ExpectedStep { ToolName = "Step1Tool" },
                    new ExpectedStep { ToolName = "Step2Tool" },
                    new ExpectedStep { ToolName = "Step3Tool" }
                }
            }
        };
        
        // Act
        var result = await benchmark.RunMultiStepReasoningBenchmarkAsync(testCases);
        
        // Assert
        Assert.Equal(0, result.PassedTests);
        Assert.Single(result.Results);
        Assert.Equal(2, result.Results[0].CompletedSteps);
        Assert.Equal(3, result.Results[0].TotalSteps);
    }

    #endregion

    #region Constructor Tests

    [Fact]
    public void Constructor_NullAgent_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new AgenticBenchmark(null!));
    }
    
    [Fact]
    public void Constructor_WithOptions_Applied()
    {
        // Arrange
        var mockAgent = new MockTestableAgent("Test", "Response");
        var options = new AgenticBenchmarkOptions { Verbose = true };
        
        // Act
        var benchmark = new AgenticBenchmark(mockAgent, options: options);
        
        // Assert - no exception, options applied
        Assert.NotNull(benchmark);
    }

    #endregion

    #region Task Completion Benchmark Tests

    [Fact]
    public async Task RunTaskCompletionBenchmarkAsync_WithoutEvaluator_ThrowsInvalidOperation()
    {
        // Arrange
        var mockAgent = new MockTestableAgent("TestAgent", "Response");
        var benchmark = new AgenticBenchmark(mockAgent, evaluator: null,
            options: new AgenticBenchmarkOptions { Verbose = false });

        var testCases = new[]
        {
            new TaskCompletionTestCase
            {
                Name = "Task 1",
                Prompt = "Do something",
                CompletionCriteria = ["Responds correctly"]
            }
        };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => benchmark.RunTaskCompletionBenchmarkAsync(testCases));
    }

    [Fact]
    public async Task RunTaskCompletionBenchmarkAsync_PassingScore_Passes()
    {
        // Arrange
        var mockAgent = new MockTestableAgent("TestAgent", "Great response");
        var mockEvaluator = new MockEvaluator(overallScore: 85, summary: "Good", criteria: [
            new CriterionResult { Criterion = "Is helpful", Met = true, Explanation = "Response is helpful" }
        ]);
        var benchmark = new AgenticBenchmark(mockAgent, mockEvaluator,
            new AgenticBenchmarkOptions { Verbose = false });

        var testCases = new[]
        {
            new TaskCompletionTestCase
            {
                Name = "Help Task",
                Prompt = "Help me do something",
                CompletionCriteria = ["Is helpful"],
                PassingScore = 70
            }
        };

        // Act
        var result = await benchmark.RunTaskCompletionBenchmarkAsync(testCases);

        // Assert
        Assert.Equal("TestAgent", result.AgentName);
        Assert.Equal(1, result.TotalTests);
        Assert.Equal(1, result.PassedTests);
        Assert.Equal(85.0, result.AverageScore);
        Assert.True(result.Results[0].Passed);
        Assert.Equal(85, result.Results[0].Score);
        Assert.Equal("Good", result.Results[0].EvaluationSummary);
    }

    [Fact]
    public async Task RunTaskCompletionBenchmarkAsync_FailingScore_Fails()
    {
        // Arrange
        var mockAgent = new MockTestableAgent("TestAgent", "Vague response");
        var mockEvaluator = new MockEvaluator(overallScore: 40, summary: "Below threshold", criteria: [
            new CriterionResult { Criterion = "Is helpful", Met = false, Explanation = "Response is vague" }
        ]);
        var benchmark = new AgenticBenchmark(mockAgent, mockEvaluator,
            new AgenticBenchmarkOptions { Verbose = false });

        var testCases = new[]
        {
            new TaskCompletionTestCase
            {
                Name = "Help Task",
                Prompt = "Help me do something",
                CompletionCriteria = ["Is helpful"],
                PassingScore = 70
            }
        };

        // Act
        var result = await benchmark.RunTaskCompletionBenchmarkAsync(testCases);

        // Assert
        Assert.Equal(0, result.PassedTests);
        Assert.False(result.Results[0].Passed);
        Assert.Equal(40, result.Results[0].Score);
    }

    [Fact]
    public async Task RunTaskCompletionBenchmarkAsync_AddDefaultCriteriaFalse_OnlyUserCriteria()
    {
        // Arrange
        var capturedCriteria = new List<string>();
        var mockAgent = new MockTestableAgent("TestAgent", "Great response");
        var capturingEvaluator = new CriteriaCapturingEvaluator(capturedCriteria, overallScore: 90);
        var benchmark = new AgenticBenchmark(mockAgent, capturingEvaluator,
            new AgenticBenchmarkOptions { Verbose = false, AddDefaultCompletionCriteria = false });

        var testCases = new[]
        {
            new TaskCompletionTestCase
            {
                Name = "Strict eval",
                Prompt = "Do X",
                CompletionCriteria = ["Criterion A", "Criterion B"]
            }
        };

        // Act
        await benchmark.RunTaskCompletionBenchmarkAsync(testCases);

        // Assert — only user criteria, no default "fully addresses" / "complete and actionable"
        Assert.Equal(2, capturedCriteria.Count);
        Assert.Contains("Criterion A", capturedCriteria);
        Assert.Contains("Criterion B", capturedCriteria);
        Assert.DoesNotContain(capturedCriteria, c => c.Contains("fully addresses", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RunTaskCompletionBenchmarkAsync_AddDefaultCriteriaTrue_InjectsStandardCriteria()
    {
        // Arrange
        var capturedCriteria = new List<string>();
        var mockAgent = new MockTestableAgent("TestAgent", "Great response");
        var capturingEvaluator = new CriteriaCapturingEvaluator(capturedCriteria, overallScore: 90);
        var benchmark = new AgenticBenchmark(mockAgent, capturingEvaluator,
            new AgenticBenchmarkOptions { Verbose = false, AddDefaultCompletionCriteria = true });

        var testCases = new[]
        {
            new TaskCompletionTestCase
            {
                Name = "With defaults",
                Prompt = "Do X",
                CompletionCriteria = ["User criterion"]
            }
        };

        // Act
        await benchmark.RunTaskCompletionBenchmarkAsync(testCases);

        // Assert — user criteria + 2 defaults
        Assert.Equal(3, capturedCriteria.Count);
        Assert.Contains("User criterion", capturedCriteria);
        Assert.Contains(capturedCriteria, c => c.Contains("fully addresses", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(capturedCriteria, c => c.Contains("complete and actionable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RunTaskCompletionBenchmarkAsync_MultipleTestCases_AveragesScores()
    {
        // Arrange
        var mockAgent = new MockTestableAgent("TestAgent", "Response");
        var mockEvaluator = new MockEvaluator(overallScore: 80, summary: "Good");
        var benchmark = new AgenticBenchmark(mockAgent, mockEvaluator,
            new AgenticBenchmarkOptions { Verbose = false });

        var testCases = new[]
        {
            new TaskCompletionTestCase { Name = "T1", Prompt = "P1", CompletionCriteria = ["C1"], PassingScore = 70 },
            new TaskCompletionTestCase { Name = "T2", Prompt = "P2", CompletionCriteria = ["C2"], PassingScore = 70 }
        };

        // Act
        var result = await benchmark.RunTaskCompletionBenchmarkAsync(testCases);

        // Assert
        Assert.Equal(2, result.TotalTests);
        Assert.Equal(2, result.PassedTests);
        Assert.Equal(80.0, result.AverageScore);
    }

    [Fact]
    public void TaskCompletionResult_ToString_FormatsCorrectly()
    {
        // Arrange
        var result = new TaskCompletionResult
        {
            AgentName = "TestAgent",
            TotalTests = 5,
            PassedTests = 4,
            AverageScore = 82.5
        };

        // Act
        var str = result.ToString();

        // Assert
        Assert.Contains("TestAgent", str);
        Assert.Contains("4/5", str);
        Assert.Contains("82.5", str);
    }

    #endregion

    #region DI / IToolUsageExtractor Tests

    [Fact]
    public async Task Constructor_WithCustomExtractor_UsesInjectedExtractor()
    {
        // Arrange
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "InjectedTool", CallId = "c1" });
        var customExtractor = new TestToolUsageExtractor(report);
        var mockAgent = new MockTestableAgent("TestAgent", "Response");
        var benchmark = new AgenticBenchmark(mockAgent, options: new AgenticBenchmarkOptions { Verbose = false },
            toolUsageExtractor: customExtractor);

        var testCases = new[]
        {
            new ToolAccuracyTestCase
            {
                Name = "DI Test",
                Prompt = "Test prompt",
                ExpectedTools = [new ExpectedTool { Name = "InjectedTool" }]
            }
        };

        // Act
        var result = await benchmark.RunToolAccuracyBenchmarkAsync(testCases);

        // Assert
        Assert.Equal(1, result.PassedTests);
        Assert.True(result.Results[0].Passed);
    }

    #endregion

    #region Result ToString Tests

    [Fact]
    public void ToolAccuracyResult_ToString_FormatsCorrectly()
    {
        // Arrange
        var result = new ToolAccuracyResult
        {
            AgentName = "TestAgent",
            TotalTests = 10,
            PassedTests = 8,
            OverallAccuracy = 0.8
        };

        // Act
        var str = result.ToString();

        // Assert
        Assert.Contains("TestAgent", str);
        Assert.Contains("8/10", str);
        Assert.Contains("80.0%", str);
    }

    [Fact]
    public void MultiStepReasoningResult_ToString_FormatsCorrectly()
    {
        // Arrange
        var result = new MultiStepReasoningResult
        {
            AgentName = "TestAgent",
            TotalTests = 5,
            PassedTests = 3,
            AverageStepCompletion = 0.75
        };

        // Act
        var str = result.ToString();

        // Assert
        Assert.Contains("TestAgent", str);
        Assert.Contains("3/5", str);
        Assert.Contains("75.0%", str);
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public async Task RunToolAccuracyBenchmarkAsync_AgentThrows_RecordsError()
    {
        // Arrange
        var throwingAgent = new ThrowingTestAgent("FailAgent", new InvalidOperationException("Agent crashed"));
        var benchmark = new AgenticBenchmark(throwingAgent, options: new AgenticBenchmarkOptions { Verbose = false });

        var testCases = new[]
        {
            new ToolAccuracyTestCase
            {
                Name = "Crashing Test",
                Prompt = "Crash now",
                ExpectedTools = [new ExpectedTool { Name = "AnyTool" }]
            }
        };

        // Act
        var result = await benchmark.RunToolAccuracyBenchmarkAsync(testCases);

        // Assert — error is caught per test case, not thrown
        Assert.Equal(1, result.TotalTests);
        Assert.Equal(0, result.PassedTests);
        Assert.False(result.Results[0].Passed);
        Assert.Contains("Agent crashed", result.Results[0].Error);
    }

    [Fact]
    public async Task RunMultiStepReasoningBenchmarkAsync_AgentThrows_RecordsError()
    {
        // Arrange
        var throwingAgent = new ThrowingTestAgent("FailAgent", new InvalidOperationException("Fail"));
        var benchmark = new AgenticBenchmark(throwingAgent, options: new AgenticBenchmarkOptions { Verbose = false });

        var testCases = new[]
        {
            new MultiStepTestCase
            {
                Name = "Crashing Workflow",
                Prompt = "Crash",
                ExpectedSteps = [new ExpectedStep { ToolName = "Step1" }]
            }
        };

        // Act
        var result = await benchmark.RunMultiStepReasoningBenchmarkAsync(testCases);

        // Assert
        Assert.Equal(0, result.PassedTests);
        Assert.Contains("Fail", result.Results[0].Error);
    }

    [Fact]
    public async Task RunTaskCompletionBenchmarkAsync_AgentThrows_RecordsError()
    {
        // Arrange
        var throwingAgent = new ThrowingTestAgent("FailAgent", new InvalidOperationException("Evaluation blown"));
        var mockEvaluator = new MockEvaluator(overallScore: 90);
        var benchmark = new AgenticBenchmark(throwingAgent, mockEvaluator,
            new AgenticBenchmarkOptions { Verbose = false });

        var testCases = new[]
        {
            new TaskCompletionTestCase
            {
                Name = "Failing Task",
                Prompt = "Do something",
                CompletionCriteria = ["Criterion A"]
            }
        };

        // Act
        var result = await benchmark.RunTaskCompletionBenchmarkAsync(testCases);

        // Assert
        Assert.Equal(1, result.TotalTests);
        Assert.Equal(0, result.PassedTests);
        Assert.Equal(0, result.Results[0].Score);
        Assert.Contains("Evaluation blown", result.Results[0].Error);
    }

    #endregion

    #region Test Helpers

    /// <summary>
    /// Agent that always throws for error handling tests.
    /// </summary>
    private sealed class ThrowingTestAgent : IEvaluableAgent
    {
        private readonly Exception _exception;
        public string Name { get; }
        public ThrowingTestAgent(string name, Exception exception) { Name = name; _exception = exception; }
        public Task<AgentResponse> InvokeAsync(string input, CancellationToken cancellationToken = default)
            => Task.FromException<AgentResponse>(_exception);
    }

    /// <summary>
    /// Mock evaluator that returns a fixed score for testing task completion.
    /// </summary>
    private sealed class MockEvaluator : IEvaluator
    {
        private readonly int _overallScore;
        private readonly string _summary;
        private readonly IReadOnlyList<CriterionResult> _criteria;

        public MockEvaluator(int overallScore, string summary = "Mock evaluation",
            IReadOnlyList<CriterionResult>? criteria = null)
        {
            _overallScore = overallScore;
            _summary = summary;
            _criteria = criteria ?? [];
        }

        public Task<EvaluationResult> EvaluateAsync(string input, string output,
            IEnumerable<string> criteria, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new EvaluationResult
            {
                OverallScore = _overallScore,
                Summary = _summary,
                CriteriaResults = _criteria
            });
        }
    }

    /// <summary>
    /// Evaluator that captures the criteria passed to it, for verifying AddDefaultCompletionCriteria.
    /// </summary>
    private sealed class CriteriaCapturingEvaluator : IEvaluator
    {
        private readonly List<string> _capturedCriteria;
        private readonly int _overallScore;

        public CriteriaCapturingEvaluator(List<string> capturedCriteria, int overallScore)
        {
            _capturedCriteria = capturedCriteria;
            _overallScore = overallScore;
        }

        public Task<EvaluationResult> EvaluateAsync(string input, string output,
            IEnumerable<string> criteria, CancellationToken cancellationToken = default)
        {
            _capturedCriteria.Clear();
            _capturedCriteria.AddRange(criteria);
            return Task.FromResult(new EvaluationResult
            {
                OverallScore = _overallScore,
                Summary = "Captured"
            });
        }
    }

    /// <summary>
    /// Test-only IToolUsageExtractor that returns a fixed ToolUsageReport.
    /// </summary>
    private sealed class TestToolUsageExtractor : IToolUsageExtractor
    {
        private readonly ToolUsageReport _fixed;
        public TestToolUsageExtractor(ToolUsageReport fixedResult) => _fixed = fixedResult;
        public ToolUsageReport Extract(IReadOnlyList<object>? rawMessages) => _fixed;
        public ToolUsageReport Extract(AgentResponse response) => _fixed;
    }

    #endregion
}
