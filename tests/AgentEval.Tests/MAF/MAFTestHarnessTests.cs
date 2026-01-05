// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.Testing;
using Xunit;

namespace AgentEval.Tests;

/// <summary>
/// Tests for MAFTestHarness.
/// </summary>
public class MAFTestHarnessTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_DefaultVerbose_CreatesHarness()
    {
        // Act
        var harness = new MAFTestHarness(verbose: true);
        
        // Assert
        Assert.NotNull(harness);
    }
    
    [Fact]
    public void Constructor_NonVerbose_CreatesHarness()
    {
        // Act
        var harness = new MAFTestHarness(verbose: false);
        
        // Assert
        Assert.NotNull(harness);
    }
    
    [Fact]
    public void Constructor_WithChatClient_CreatesHarness()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        
        // Act
        var harness = new MAFTestHarness(fakeChatClient, verbose: false);
        
        // Assert
        Assert.NotNull(harness);
    }
    
    [Fact]
    public void Constructor_WithCustomEvaluatorAndLogger_CreatesHarness()
    {
        // Arrange
        var evaluator = new MockEvaluator();
        var logger = NullAgentEvalLogger.Instance;
        
        // Act
        var harness = new MAFTestHarness(evaluator, logger);
        
        // Assert
        Assert.NotNull(harness);
    }

    #endregion

    #region RunTestAsync Tests

    [Fact]
    public async Task RunTestAsync_SimpleTest_ReturnsResult()
    {
        // Arrange
        var harness = new MAFTestHarness(verbose: false);
        var mockAgent = new SimpleTestableAgent("TestAgent", "Hello, World!");
        var testCase = new TestCase
        {
            Name = "Simple Test",
            Input = "Say hello"
        };
        
        // Act
        var result = await harness.RunTestAsync(mockAgent, testCase);
        
        // Assert
        Assert.Equal("Simple Test", result.TestName);
        Assert.True(result.Passed);
        Assert.Equal("Hello, World!", result.ActualOutput);
    }
    
    [Fact]
    public async Task RunTestAsync_EmptyOutput_Fails()
    {
        // Arrange
        var harness = new MAFTestHarness(verbose: false);
        var mockAgent = new SimpleTestableAgent("TestAgent", "");
        var testCase = new TestCase
        {
            Name = "Empty Output Test",
            Input = "Do something"
        };
        
        // Act
        var result = await harness.RunTestAsync(mockAgent, testCase);
        
        // Assert
        Assert.False(result.Passed);
        Assert.Equal(0, result.Score);
    }
    
    [Fact]
    public async Task RunTestAsync_WithExpectedOutputContains_PassesWhenContains()
    {
        // Arrange
        var harness = new MAFTestHarness(verbose: false);
        var mockAgent = new SimpleTestableAgent("TestAgent", "The capital of France is Paris.");
        var testCase = new TestCase
        {
            Name = "Contains Test",
            Input = "What is the capital of France?",
            ExpectedOutputContains = "Paris"
        };
        
        // Act
        var result = await harness.RunTestAsync(mockAgent, testCase);
        
        // Assert
        Assert.True(result.Passed);
    }
    
    [Fact]
    public async Task RunTestAsync_WithExpectedOutputContains_FailsWhenMissing()
    {
        // Arrange
        var harness = new MAFTestHarness(verbose: false);
        var mockAgent = new SimpleTestableAgent("TestAgent", "The capital is unknown.");
        var testCase = new TestCase
        {
            Name = "Missing Contains Test",
            Input = "What is the capital of France?",
            ExpectedOutputContains = "Paris"
        };
        
        // Act
        var result = await harness.RunTestAsync(mockAgent, testCase);
        
        // Assert
        Assert.False(result.Passed);
        Assert.Contains("Paris", result.Details);
    }
    
    [Fact]
    public async Task RunTestAsync_WithPerformanceTracking_CapturesMetrics()
    {
        // Arrange
        var harness = new MAFTestHarness(verbose: false);
        var mockAgent = new SimpleTestableAgent("TestAgent", "Done!");
        var testCase = new TestCase
        {
            Name = "Performance Test",
            Input = "Do something"
        };
        var options = new TestOptions { TrackPerformance = true };
        
        // Act
        var result = await harness.RunTestAsync(mockAgent, testCase, options);
        
        // Assert
        Assert.NotNull(result.Performance);
        Assert.NotNull(result.Performance.StartTime);
        Assert.NotNull(result.Performance.EndTime);
    }
    
    [Fact]
    public async Task RunTestAsync_AgentThrowsException_CapturesError()
    {
        // Arrange
        var harness = new MAFTestHarness(verbose: false);
        var mockAgent = new ThrowingTestableAgent("ThrowingAgent", new InvalidOperationException("Test error"));
        var testCase = new TestCase
        {
            Name = "Error Test",
            Input = "Trigger error"
        };
        
        // Act
        var result = await harness.RunTestAsync(mockAgent, testCase);
        
        // Assert
        Assert.False(result.Passed);
        Assert.Equal(0, result.Score);
        Assert.NotNull(result.Error);
        Assert.Contains("Test error", result.Details);
        Assert.NotNull(result.Failure);
    }
    
    [Fact]
    public async Task RunTestAsync_WithToolTracking_ExtractsToolUsage()
    {
        // Arrange
        var harness = new MAFTestHarness(verbose: false);
        var mockAgent = new AgentWithToolCalls("ToolAgent", "Result", new[] { "Tool1", "Tool2" });
        var testCase = new TestCase
        {
            Name = "Tool Tracking Test",
            Input = "Use tools"
        };
        var options = new TestOptions { TrackTools = true };
        
        // Act
        var result = await harness.RunTestAsync(mockAgent, testCase, options);
        
        // Assert
        Assert.True(result.Passed);
        Assert.NotNull(result.ToolUsage);
        Assert.Equal(2, result.ToolUsage.Count);
    }

    #endregion

    #region RunTestSuiteAsync Tests

    [Fact]
    public async Task RunTestSuiteAsync_MultipleTests_ReturnsSummary()
    {
        // Arrange
        var harness = new MAFTestHarness(verbose: false);
        var mockAgent = new SimpleTestableAgent("TestAgent", "Done!");
        var testCases = new[]
        {
            new TestCase { Name = "Test 1", Input = "Input 1" },
            new TestCase { Name = "Test 2", Input = "Input 2" },
            new TestCase { Name = "Test 3", Input = "Input 3" }
        };
        
        // Act
        var summary = await harness.RunTestSuiteAsync("Test Suite", mockAgent, testCases);
        
        // Assert
        Assert.Equal("Test Suite", summary.SuiteName);
        Assert.Equal(3, summary.TotalCount);
        Assert.Equal(3, summary.PassedCount);
        Assert.True(summary.AllPassed);
        Assert.Equal(100, summary.AverageScore);
    }
    
    [Fact]
    public async Task RunTestSuiteAsync_MixedResults_CalculatesCorrectly()
    {
        // Arrange
        var harness = new MAFTestHarness(verbose: false);
        var alternatingAgent = new AlternatingTestableAgent();
        var testCases = new[]
        {
            new TestCase { Name = "Pass Test", Input = "pass" },
            new TestCase { Name = "Fail Test", Input = "fail" }
        };
        
        // Act
        var summary = await harness.RunTestSuiteAsync("Mixed Suite", alternatingAgent, testCases);
        
        // Assert
        Assert.Equal(2, summary.TotalCount);
        Assert.Equal(1, summary.PassedCount);
        Assert.Equal(1, summary.FailedCount);
        Assert.False(summary.AllPassed);
    }

    #endregion
}

#region Test Helpers

internal class SimpleTestableAgent : ITestableAgent
{
    private readonly string _responseText;
    
    public string Name { get; }
    
    public SimpleTestableAgent(string name, string responseText)
    {
        Name = name;
        _responseText = responseText;
    }
    
    public Task<AgentResponse> InvokeAsync(string input, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new AgentResponse { Text = _responseText });
    }
}

internal class ThrowingTestableAgent : ITestableAgent
{
    private readonly Exception _exception;
    
    public string Name { get; }
    
    public ThrowingTestableAgent(string name, Exception exception)
    {
        Name = name;
        _exception = exception;
    }
    
    public Task<AgentResponse> InvokeAsync(string input, CancellationToken cancellationToken = default)
    {
        throw _exception;
    }
}

internal class AgentWithToolCalls : ITestableAgent
{
    private readonly string _responseText;
    private readonly string[] _toolNames;
    
    public string Name { get; }
    
    public AgentWithToolCalls(string name, string responseText, string[] toolNames)
    {
        Name = name;
        _responseText = responseText;
        _toolNames = toolNames;
    }
    
    public Task<AgentResponse> InvokeAsync(string input, CancellationToken cancellationToken = default)
    {
        var messages = new List<Microsoft.Extensions.AI.ChatMessage>();
        
        foreach (var toolName in _toolNames)
        {
            var msg = new Microsoft.Extensions.AI.ChatMessage(
                Microsoft.Extensions.AI.ChatRole.Assistant,
                $"Calling {toolName}");
            
            var funcCall = new Microsoft.Extensions.AI.FunctionCallContent(
                callId: Guid.NewGuid().ToString(),
                name: toolName);
            msg.Contents.Add(funcCall);
            messages.Add(msg);
            
            var funcResult = new Microsoft.Extensions.AI.FunctionResultContent(funcCall.CallId, "Result");
            var resultMsg = new Microsoft.Extensions.AI.ChatMessage(
                Microsoft.Extensions.AI.ChatRole.Tool,
                new Microsoft.Extensions.AI.AIContent[] { funcResult });
            messages.Add(resultMsg);
        }
        
        messages.Add(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.Assistant,
            _responseText));
        
        return Task.FromResult(new AgentResponse
        {
            Text = _responseText,
            RawMessages = messages
        });
    }
}

internal class AlternatingTestableAgent : ITestableAgent
{
    public string Name => "AlternatingAgent";
    
    public Task<AgentResponse> InvokeAsync(string input, CancellationToken cancellationToken = default)
    {
        // Return non-empty for "pass", empty for "fail"
        var text = input.Contains("pass", StringComparison.OrdinalIgnoreCase) ? "Passed!" : "";
        return Task.FromResult(new AgentResponse { Text = text });
    }
}

internal class MockEvaluator : IEvaluator
{
    public Task<EvaluationResult> EvaluateAsync(
        string input, 
        string output, 
        IEnumerable<string> criteria, 
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new EvaluationResult
        {
            OverallScore = 100,
            Summary = "Mock evaluation passed"
        });
    }
}

#endregion
