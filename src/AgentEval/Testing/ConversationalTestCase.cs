// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Testing;

/// <summary>
/// Represents a single turn in a multi-turn conversation.
/// </summary>
/// <param name="Role">The role of the speaker (user, assistant, system, tool).</param>
/// <param name="Content">The text content of this turn.</param>
/// <param name="ToolCalls">Optional tool calls made during this turn (for assistant turns).</param>
/// <param name="ToolCallId">Optional tool call ID (for tool response turns).</param>
public record Turn(
    string Role,
    string Content,
    IReadOnlyList<ToolCallInfo>? ToolCalls = null,
    string? ToolCallId = null)
{
    /// <summary>Creates a user turn.</summary>
    public static Turn User(string content) => new("user", content);
    
    /// <summary>Creates an assistant turn.</summary>
    public static Turn Assistant(string content, params ToolCallInfo[] toolCalls) => 
        new("assistant", content, toolCalls.Length > 0 ? toolCalls : null);
    
    /// <summary>Creates a system turn.</summary>
    public static Turn System(string content) => new("system", content);
    
    /// <summary>Creates a tool response turn.</summary>
    public static Turn Tool(string content, string toolCallId) => 
        new("tool", content, null, toolCallId);
}

/// <summary>
/// Information about a tool call made by the assistant.
/// </summary>
/// <param name="Name">The name of the tool/function called.</param>
/// <param name="Arguments">The arguments passed to the tool.</param>
/// <param name="Id">Optional unique identifier for this tool call.</param>
public record ToolCallInfo(
    string Name,
    IReadOnlyDictionary<string, object?>? Arguments = null,
    string? Id = null);

/// <summary>
/// Represents a multi-turn conversation test case.
/// </summary>
public class ConversationalTestCase
{
    /// <summary>Unique identifier for this test case.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    
    /// <summary>Human-readable name for the test case.</summary>
    public string Name { get; set; } = "";
    
    /// <summary>Description of what this test validates.</summary>
    public string? Description { get; set; }
    
    /// <summary>Category or group for organizing tests.</summary>
    public string? Category { get; set; }
    
    /// <summary>The ordered list of turns in this conversation.</summary>
    public List<Turn> Turns { get; set; } = new();
    
    /// <summary>Expected tools that should be called during the conversation.</summary>
    public List<string>? ExpectedTools { get; set; }
    
    /// <summary>Expected final outcome or assertion.</summary>
    public string? ExpectedOutcome { get; set; }
    
    /// <summary>Maximum allowed total duration for the conversation.</summary>
    public TimeSpan? MaxDuration { get; set; }
    
    /// <summary>Custom metadata for this test case.</summary>
    public Dictionary<string, object?> Metadata { get; set; } = new();
    
    /// <summary>
    /// Creates a new conversational test case with a fluent builder.
    /// </summary>
    public static ConversationalTestCaseBuilder Create(string name) => new(name);
}

/// <summary>
/// Fluent builder for creating conversational test cases.
/// </summary>
public class ConversationalTestCaseBuilder
{
    private readonly ConversationalTestCase _testCase;

    internal ConversationalTestCaseBuilder(string name)
    {
        _testCase = new ConversationalTestCase { Name = name };
    }

    /// <summary>Sets the test case ID.</summary>
    public ConversationalTestCaseBuilder WithId(string id)
    {
        _testCase.Id = id;
        return this;
    }

    /// <summary>Sets the description.</summary>
    public ConversationalTestCaseBuilder WithDescription(string description)
    {
        _testCase.Description = description;
        return this;
    }

    /// <summary>Sets the category.</summary>
    public ConversationalTestCaseBuilder InCategory(string category)
    {
        _testCase.Category = category;
        return this;
    }

    /// <summary>Adds a system message at the start.</summary>
    public ConversationalTestCaseBuilder WithSystemPrompt(string prompt)
    {
        _testCase.Turns.Insert(0, Turn.System(prompt));
        return this;
    }

    /// <summary>Adds a user turn.</summary>
    public ConversationalTestCaseBuilder AddUserTurn(string content)
    {
        _testCase.Turns.Add(Turn.User(content));
        return this;
    }

    /// <summary>Adds an expected assistant turn.</summary>
    public ConversationalTestCaseBuilder AddAssistantTurn(string content, params ToolCallInfo[] toolCalls)
    {
        _testCase.Turns.Add(Turn.Assistant(content, toolCalls));
        return this;
    }

    /// <summary>Adds a tool response turn.</summary>
    public ConversationalTestCaseBuilder AddToolResponse(string content, string toolCallId)
    {
        _testCase.Turns.Add(Turn.Tool(content, toolCallId));
        return this;
    }

    /// <summary>Sets expected tools to be called.</summary>
    public ConversationalTestCaseBuilder ExpectTools(params string[] tools)
    {
        _testCase.ExpectedTools = tools.ToList();
        return this;
    }

    /// <summary>Sets the expected outcome.</summary>
    public ConversationalTestCaseBuilder ExpectOutcome(string outcome)
    {
        _testCase.ExpectedOutcome = outcome;
        return this;
    }

    /// <summary>Sets maximum duration constraint.</summary>
    public ConversationalTestCaseBuilder WithMaxDuration(TimeSpan duration)
    {
        _testCase.MaxDuration = duration;
        return this;
    }

    /// <summary>Adds custom metadata.</summary>
    public ConversationalTestCaseBuilder WithMetadata(string key, object? value)
    {
        _testCase.Metadata[key] = value;
        return this;
    }

    /// <summary>Builds the test case.</summary>
    public ConversationalTestCase Build() => _testCase;
}
