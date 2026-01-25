// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Models;
using AgentEval.Testing;
using Xunit;

namespace AgentEval.Tests;

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
        Assert.Contains("80", str);
    }
    
    [Fact]
    public void MultiStepReasoningResult_ToString_FormatsCorrectly()
    {
        // Arrange
        var result = new MultiStepReasoningResult
        {
            AgentName = "TestAgent",
            TotalTests = 5,
            PassedTests = 4,
            AverageStepCompletion = 0.9
        };
        
        // Act
        var str = result.ToString();
        
        // Assert
        Assert.Contains("TestAgent", str);
        Assert.Contains("4/5", str);
    }

    #endregion
}

/// <summary>
/// Mock testable agent for testing.
/// </summary>
internal class MockTestableAgent : IEvaluableAgent
{
    private readonly string _responseText;
    private readonly (string Name, Dictionary<string, object?> Args)[] _toolCalls;
    
    public string Name { get; }
    
    public MockTestableAgent(
        string name, 
        string responseText, 
        params (string Name, Dictionary<string, object?> Args)[] toolCalls)
    {
        Name = name;
        _responseText = responseText;
        _toolCalls = toolCalls;
    }
    
    public Task<AgentResponse> InvokeAsync(string input, CancellationToken cancellationToken = default)
    {
        var messages = new List<Microsoft.Extensions.AI.ChatMessage>();
        
        // Add tool call messages if any
        foreach (var (toolName, args) in _toolCalls)
        {
            var toolCallMsg = new Microsoft.Extensions.AI.ChatMessage(
                Microsoft.Extensions.AI.ChatRole.Assistant,
                $"Calling {toolName}");
            
            // Add function call content
            var funcCall = new Microsoft.Extensions.AI.FunctionCallContent(
                callId: Guid.NewGuid().ToString(),
                name: toolName,
                arguments: args);
            toolCallMsg.Contents.Add(funcCall);
            messages.Add(toolCallMsg);
            
            // Add function result
            var funcResult = new Microsoft.Extensions.AI.FunctionResultContent(funcCall.CallId, "Result");
            var resultMsg = new Microsoft.Extensions.AI.ChatMessage(
                Microsoft.Extensions.AI.ChatRole.Tool,
                new Microsoft.Extensions.AI.AIContent[] { funcResult });
            messages.Add(resultMsg);
        }
        
        // Add final response
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
