// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Output;

namespace AgentEval.Tests.Output;

public class TimeTravelTraceTests
{
    [Fact]
    public void TimeTravelTrace_CanSerializeToJson()
    {
        var trace = CreateSampleTrace();
        
        var json = JsonSerializer.Serialize(trace, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        
        Assert.NotNull(json);
        Assert.Contains("\"schemaVersion\":", json);
        Assert.Contains("\"1.0\"", json);
        Assert.Contains("\"traceId\":", json);
        Assert.Contains("\"executionType\":", json);
    }

    [Fact]
    public void TimeTravelTrace_CanDeserializeFromJson()
    {
        var original = CreateSampleTrace();
        
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        
        var json = JsonSerializer.Serialize(original, options);
        var deserialized = JsonSerializer.Deserialize<TimeTravelTrace>(json, options);
        
        Assert.NotNull(deserialized);
        Assert.Equal(original.TraceId, deserialized.TraceId);
        Assert.Equal(original.ExecutionType, deserialized.ExecutionType);
        Assert.Equal(original.SchemaVersion, deserialized.SchemaVersion);
    }

    [Fact]
    public void ExecutionType_SerializesAsString()
    {
        var trace = CreateSampleTrace();
        
        var json = JsonSerializer.Serialize(trace);
        
        Assert.Contains("SingleAgent", json);
        Assert.DoesNotContain("\"0\"", json); // Should not be numeric
    }

    [Fact]
    public void StepType_SerializesAsString()
    {
        var step = new ExecutionStep
        {
            StepNumber = 1,
            Type = StepType.UserInput,
            Timestamp = DateTimeOffset.UtcNow,
            OffsetFromStart = TimeSpan.Zero,
            Duration = TimeSpan.FromMilliseconds(100),
            Data = new UserInputStepData { Message = "Hello" }
        };
        
        var json = JsonSerializer.Serialize(step);
        
        Assert.Contains("UserInput", json);
    }

    [Fact]
    public void TestMetadata_ContainsRequiredFields()
    {
        var metadata = new EvaluationMetadata
        {
            TestName = "MyTest",
            StartTime = DateTimeOffset.UtcNow.AddSeconds(-1),
            EndTime = DateTimeOffset.UtcNow,
            Passed = true
        };
        
        var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        
        Assert.Contains("\"testName\":", json);
        Assert.Contains("\"startTime\":", json);
        Assert.Contains("\"endTime\":", json);
        Assert.Contains("\"passed\":", json);
    }

    [Fact]
    public void AgentInfo_CanIncludeTools()
    {
        var agent = new AgentInfo
        {
            AgentId = "agent_1",
            AgentName = "TestAgent",
            ModelId = "gpt-4o",
            AvailableTools = new List<ToolDefinition>
            {
                new()
                {
                    Name = "GetWeather",
                    Description = "Gets the weather for a location",
                    Parameters = new Dictionary<string, ToolParameter>
                    {
                        ["city"] = new ToolParameter 
                        { 
                            Type = "string", 
                            Description = "The city name",
                            Required = true
                        }
                    }
                }
            }
        };
        
        var json = JsonSerializer.Serialize(agent, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        
        Assert.Contains("GetWeather", json);
        Assert.Contains("\"parameters\":", json);
    }

    [Fact]
    public void ExecutionStep_SupportsAllStepTypes()
    {
        var stepTypes = Enum.GetValues<StepType>();
        
        Assert.Contains(StepType.UserInput, stepTypes);
        Assert.Contains(StepType.SystemPrompt, stepTypes);
        Assert.Contains(StepType.LlmRequest, stepTypes);
        Assert.Contains(StepType.LlmResponse, stepTypes);
        Assert.Contains(StepType.LlmStreamStart, stepTypes);
        Assert.Contains(StepType.LlmStreamChunk, stepTypes);
        Assert.Contains(StepType.LlmStreamEnd, stepTypes);
        Assert.Contains(StepType.ToolCall, stepTypes);
        Assert.Contains(StepType.ToolResult, stepTypes);
        Assert.Contains(StepType.AgentHandoff, stepTypes);
        Assert.Contains(StepType.AgentResponse, stepTypes);
        Assert.Contains(StepType.Error, stepTypes);
        Assert.Contains(StepType.Assertion, stepTypes);
    }

    [Fact]
    public void ExecutionSummary_CalculatesTotalTokens()
    {
        var summary = new ExecutionSummary
        {
            Passed = true,
            TotalDuration = TimeSpan.FromSeconds(1),
            TotalSteps = 5,
            ToolCallCount = 2,
            ToolErrorCount = 0,
            LlmRequestCount = 2,
            TotalTokenUsage = new TokenUsageData
            {
                InputTokens = 100,
                OutputTokens = 50
            }
        };
        
        Assert.Equal(150, summary.TotalTokenUsage.TotalTokens);
    }

    [Fact]
    public void ToolCallStepData_ContainsArguments()
    {
        var toolCall = new ToolCallStepData
        {
            ToolCallId = "call_1",
            ToolName = "GetWeather",
            Arguments = new Dictionary<string, object?> { ["city"] = "Seattle" }
        };
        
        var json = JsonSerializer.Serialize(toolCall, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        
        Assert.Contains("GetWeather", json);
        Assert.Contains("Seattle", json);
    }

    [Fact]
    public void ToolResultStepData_IncludesErrorInfo()
    {
        var toolResult = new ToolResultStepData
        {
            ToolCallId = "call_1",
            ToolName = "GetWeather",
            Result = "",
            IsError = true,
            ErrorMessage = "Network timeout",
            ExecutionDuration = TimeSpan.FromMilliseconds(5000)
        };
        
        var json = JsonSerializer.Serialize(toolResult, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        
        Assert.Contains("\"isError\":true", json);
        Assert.Contains("Network timeout", json);
    }

    [Fact]
    public void AgentHandoffStepData_TracksFromAndTo()
    {
        var handoff = new AgentHandoffStepData
        {
            FromAgentId = "orchestrator",
            ToAgentId = "researcher",
            HandoffReason = "Need to research topic",
            Context = new Dictionary<string, object> { ["topic"] = "AI agents" }
        };
        
        var json = JsonSerializer.Serialize(handoff, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        
        Assert.Contains("orchestrator", json);
        Assert.Contains("researcher", json);
        Assert.Contains("AI agents", json);
    }

    [Fact]
    public void AssertionStepData_IncludesExpectedAndActual()
    {
        var assertion = new AssertionStepData
        {
            AssertionType = "HaveCalledTool",
            Passed = false,
            Expected = "GetWeather",
            Actual = "(no tool called)",
            Because = "weather lookup is required",
            Suggestions = new List<string>
            {
                "Ensure the agent has the GetWeather tool available",
                "Check if the prompt mentions weather"
            }
        };
        
        var json = JsonSerializer.Serialize(assertion, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        
        Assert.Contains("HaveCalledTool", json);
        Assert.Contains("GetWeather", json);
        Assert.Contains("weather lookup is required", json);
        Assert.Contains("Ensure the agent", json);
    }

    [Fact]
    public void LlmResponseStepData_IncludesToolCalls()
    {
        var response = new LlmResponseStepData
        {
            Content = "I'll check the weather for you.",
            ToolCalls = new List<ToolCallStepData>
            {
                new()
                {
                    ToolCallId = "call_1",
                    ToolName = "GetWeather",
                    Arguments = new Dictionary<string, object?> { ["city"] = "Seattle" }
                }
            },
            TokenUsage = new TokenUsageData { InputTokens = 50, OutputTokens = 20 },
            FinishReason = "tool_calls"
        };
        
        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        
        Assert.Contains("GetWeather", json);
        Assert.Contains("tool_calls", json);
    }

    #region Test Helpers

    private static TimeTravelTrace CreateSampleTrace()
    {
        var startTime = DateTimeOffset.UtcNow.AddSeconds(-2);
        var endTime = DateTimeOffset.UtcNow;

        return new TimeTravelTrace
        {
            TraceId = "trace_123",
            ExecutionType = ExecutionType.SingleAgent,
            Test = new EvaluationMetadata
            {
                TestName = "WeatherAgentTest",
                StartTime = startTime,
                EndTime = endTime,
                Passed = true
            },
            Agents = new List<AgentInfo>
            {
                new()
                {
                    AgentId = "agent_1",
                    AgentName = "WeatherAgent",
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
                    Data = new UserInputStepData { Message = "What's the weather in Seattle?" }
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
