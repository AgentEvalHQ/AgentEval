// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace AgentEval.Output;

/// <summary>
/// Complete time-travel trace for any execution type.
/// Designed for UI visualization and step-by-step replay.
/// </summary>
public class TimeTravelTrace
{
    /// <summary>Schema version for forward compatibility.</summary>
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "1.0";

    /// <summary>Unique identifier for this trace.</summary>
    [JsonPropertyName("traceId")]
    public required string TraceId { get; init; }

    /// <summary>Type of execution: SingleAgent, Chat, Workflow.</summary>
    [JsonPropertyName("executionType")]
    public required ExecutionType ExecutionType { get; init; }

    /// <summary>Test metadata.</summary>
    [JsonPropertyName("test")]
    public required EvaluationMetadata Test { get; init; }

    /// <summary>Execution timeline - every step in order.</summary>
    [JsonPropertyName("steps")]
    public required List<ExecutionStep> Steps { get; init; }

    /// <summary>Agent(s) involved in this execution.</summary>
    [JsonPropertyName("agents")]
    public required List<AgentInfo> Agents { get; init; }

    /// <summary>Final result summary.</summary>
    [JsonPropertyName("summary")]
    public required ExecutionSummary Summary { get; init; }
}

/// <summary>
/// Type of agent execution being traced.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExecutionType
{
    /// <summary>Single agent responding to a single input.</summary>
    SingleAgent,

    /// <summary>Multi-turn conversation with an agent.</summary>
    Chat,

    /// <summary>Multi-agent workflow with handoffs.</summary>
    Workflow
}

/// <summary>
/// Metadata about the test that generated this trace.
/// </summary>
public class EvaluationMetadata
{
    /// <summary>Name of the test.</summary>
    [JsonPropertyName("testName")]
    public required string TestName { get; init; }

    /// <summary>Test class name (if applicable).</summary>
    [JsonPropertyName("testClass")]
    public string? TestClass { get; init; }

    /// <summary>When execution started.</summary>
    [JsonPropertyName("startTime")]
    public DateTimeOffset StartTime { get; init; }

    /// <summary>When execution completed.</summary>
    [JsonPropertyName("endTime")]
    public DateTimeOffset EndTime { get; init; }

    /// <summary>Whether all assertions passed.</summary>
    [JsonPropertyName("passed")]
    public bool Passed { get; init; }

    /// <summary>Failure message if test failed.</summary>
    [JsonPropertyName("failureMessage")]
    public string? FailureMessage { get; init; }

    /// <summary>Custom tags/labels for categorization.</summary>
    [JsonPropertyName("tags")]
    public Dictionary<string, string>? Tags { get; init; }
}

/// <summary>
/// Information about an agent in the execution.
/// </summary>
public class AgentInfo
{
    /// <summary>Unique identifier for this agent.</summary>
    [JsonPropertyName("agentId")]
    public required string AgentId { get; init; }

    /// <summary>Human-readable name.</summary>
    [JsonPropertyName("agentName")]
    public required string AgentName { get; init; }

    /// <summary>Model identifier (e.g., gpt-4o).</summary>
    [JsonPropertyName("modelId")]
    public string? ModelId { get; init; }

    /// <summary>System prompt used.</summary>
    [JsonPropertyName("systemPrompt")]
    public string? SystemPrompt { get; init; }

    /// <summary>Tools available to this agent.</summary>
    [JsonPropertyName("availableTools")]
    public List<ToolDefinition>? AvailableTools { get; init; }
}

/// <summary>
/// Definition of a tool available to an agent.
/// </summary>
public class ToolDefinition
{
    /// <summary>Tool name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Tool description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Tool parameters.</summary>
    [JsonPropertyName("parameters")]
    public Dictionary<string, ToolParameter>? Parameters { get; init; }
}

/// <summary>
/// Parameter definition for a tool.
/// </summary>
public class ToolParameter
{
    /// <summary>Parameter type (string, integer, boolean, etc.).</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>Parameter description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Whether this parameter is required.</summary>
    [JsonPropertyName("required")]
    public bool Required { get; init; }
}

/// <summary>
/// A single step in the execution timeline.
/// </summary>
public class ExecutionStep
{
    /// <summary>Step number (1-based).</summary>
    [JsonPropertyName("stepNumber")]
    public int StepNumber { get; init; }

    /// <summary>Type of step.</summary>
    [JsonPropertyName("type")]
    public required StepType Type { get; init; }

    /// <summary>When this step started (absolute).</summary>
    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Offset from execution start (for timeline rendering).</summary>
    [JsonPropertyName("offsetFromStart")]
    public TimeSpan OffsetFromStart { get; init; }

    /// <summary>Duration of this step.</summary>
    [JsonPropertyName("duration")]
    public TimeSpan Duration { get; init; }

    /// <summary>Which agent performed this step (for workflows).</summary>
    [JsonPropertyName("agentId")]
    public string? AgentId { get; init; }

    /// <summary>Step-type-specific data.</summary>
    [JsonPropertyName("data")]
    public required object Data { get; init; }
}

/// <summary>
/// Types of execution steps.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StepType
{
    /// <summary>User input message.</summary>
    UserInput,

    /// <summary>System prompt being set.</summary>
    SystemPrompt,

    /// <summary>Request sent to LLM.</summary>
    LlmRequest,

    /// <summary>Complete response from LLM.</summary>
    LlmResponse,

    /// <summary>Start of streaming response.</summary>
    LlmStreamStart,

    /// <summary>Single chunk of streaming response.</summary>
    LlmStreamChunk,

    /// <summary>End of streaming response.</summary>
    LlmStreamEnd,

    /// <summary>Tool call initiated.</summary>
    ToolCall,

    /// <summary>Tool call result received.</summary>
    ToolResult,

    /// <summary>Handoff from one agent to another.</summary>
    AgentHandoff,

    /// <summary>Final response from agent.</summary>
    AgentResponse,

    /// <summary>Error occurred.</summary>
    Error,

    /// <summary>Assertion was evaluated.</summary>
    Assertion
}

#region Step Data Classes

/// <summary>
/// Data for a user input step.
/// </summary>
public class UserInputStepData
{
    /// <summary>The user's message.</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>Additional metadata.</summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Data for a system prompt step.
/// </summary>
public class SystemPromptStepData
{
    /// <summary>The system prompt text.</summary>
    [JsonPropertyName("prompt")]
    public required string Prompt { get; init; }
}

/// <summary>
/// Data for an LLM request step.
/// </summary>
public class LlmRequestStepData
{
    /// <summary>All messages in the request.</summary>
    [JsonPropertyName("messages")]
    public required List<MessageData> Messages { get; init; }

    /// <summary>Model used for this request.</summary>
    [JsonPropertyName("modelId")]
    public string? ModelId { get; init; }

    /// <summary>Temperature setting.</summary>
    [JsonPropertyName("temperature")]
    public double? Temperature { get; init; }

    /// <summary>Maximum tokens requested.</summary>
    [JsonPropertyName("maxTokens")]
    public int? MaxTokens { get; init; }

    /// <summary>Tools available for this request.</summary>
    [JsonPropertyName("availableTools")]
    public List<string>? AvailableTools { get; init; }
}

/// <summary>
/// A message in a conversation.
/// </summary>
public class MessageData
{
    /// <summary>Role: system, user, assistant, tool.</summary>
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    /// <summary>Message content.</summary>
    [JsonPropertyName("content")]
    public required string Content { get; init; }

    /// <summary>Tool call ID (for tool responses).</summary>
    [JsonPropertyName("toolCallId")]
    public string? ToolCallId { get; init; }

    /// <summary>Tool name (for tool responses).</summary>
    [JsonPropertyName("toolName")]
    public string? ToolName { get; init; }
}

/// <summary>
/// Data for an LLM response step.
/// </summary>
public class LlmResponseStepData
{
    /// <summary>Response content.</summary>
    [JsonPropertyName("content")]
    public required string Content { get; init; }

    /// <summary>Tool calls made in this response.</summary>
    [JsonPropertyName("toolCalls")]
    public List<ToolCallStepData>? ToolCalls { get; init; }

    /// <summary>Token usage for this response.</summary>
    [JsonPropertyName("tokenUsage")]
    public TokenUsageData? TokenUsage { get; init; }

    /// <summary>Finish reason (stop, tool_calls, length, etc.).</summary>
    [JsonPropertyName("finishReason")]
    public string? FinishReason { get; init; }
}

/// <summary>
/// Data for a streaming chunk step.
/// </summary>
public class LlmStreamChunkStepData
{
    /// <summary>Index of this chunk in the stream.</summary>
    [JsonPropertyName("chunkIndex")]
    public int ChunkIndex { get; init; }

    /// <summary>Content of this chunk.</summary>
    [JsonPropertyName("content")]
    public required string Content { get; init; }

    /// <summary>Whether this is the final chunk.</summary>
    [JsonPropertyName("isComplete")]
    public bool IsComplete { get; init; }

    /// <summary>Cumulative content so far.</summary>
    [JsonPropertyName("cumulativeContent")]
    public string? CumulativeContent { get; init; }
}

/// <summary>
/// Data for a tool call step.
/// </summary>
public class ToolCallStepData
{
    /// <summary>Unique ID for this tool call.</summary>
    [JsonPropertyName("toolCallId")]
    public required string ToolCallId { get; init; }

    /// <summary>Name of the tool being called.</summary>
    [JsonPropertyName("toolName")]
    public required string ToolName { get; init; }

    /// <summary>Arguments passed to the tool.</summary>
    [JsonPropertyName("arguments")]
    public required Dictionary<string, object?> Arguments { get; init; }

    /// <summary>Original JSON string of arguments.</summary>
    [JsonPropertyName("argumentsRaw")]
    public string? ArgumentsRaw { get; init; }
}

/// <summary>
/// Data for a tool result step.
/// </summary>
public class ToolResultStepData
{
    /// <summary>ID linking to the original tool call.</summary>
    [JsonPropertyName("toolCallId")]
    public required string ToolCallId { get; init; }

    /// <summary>Name of the tool that was called.</summary>
    [JsonPropertyName("toolName")]
    public required string ToolName { get; init; }

    /// <summary>Result returned by the tool.</summary>
    [JsonPropertyName("result")]
    public required string Result { get; init; }

    /// <summary>Whether the tool call resulted in an error.</summary>
    [JsonPropertyName("isError")]
    public bool IsError { get; init; }

    /// <summary>Error message if the call failed.</summary>
    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }

    /// <summary>How long the tool took to execute.</summary>
    [JsonPropertyName("executionDuration")]
    public TimeSpan ExecutionDuration { get; init; }
}

/// <summary>
/// Data for an agent handoff step (workflow).
/// </summary>
public class AgentHandoffStepData
{
    /// <summary>Agent handing off control.</summary>
    [JsonPropertyName("fromAgentId")]
    public required string FromAgentId { get; init; }

    /// <summary>Agent receiving control.</summary>
    [JsonPropertyName("toAgentId")]
    public required string ToAgentId { get; init; }

    /// <summary>Reason for the handoff.</summary>
    [JsonPropertyName("handoffReason")]
    public string? HandoffReason { get; init; }

    /// <summary>Context passed with the handoff.</summary>
    [JsonPropertyName("context")]
    public Dictionary<string, object>? Context { get; init; }
}

/// <summary>
/// Data for an error step.
/// </summary>
public class ErrorStepData
{
    /// <summary>Type of error or exception name.</summary>
    [JsonPropertyName("errorType")]
    public required string ErrorType { get; init; }

    /// <summary>Error message.</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>Stack trace (may be truncated).</summary>
    [JsonPropertyName("stackTrace")]
    public string? StackTrace { get; init; }

    /// <summary>Whether execution can continue.</summary>
    [JsonPropertyName("isRecoverable")]
    public bool IsRecoverable { get; init; }
}

/// <summary>
/// Data for an assertion step.
/// </summary>
public class AssertionStepData
{
    /// <summary>Type of assertion (HaveCalledTool, BeforeTool, etc.).</summary>
    [JsonPropertyName("assertionType")]
    public required string AssertionType { get; init; }

    /// <summary>Whether the assertion passed.</summary>
    [JsonPropertyName("passed")]
    public required bool Passed { get; init; }

    /// <summary>What was expected.</summary>
    [JsonPropertyName("expected")]
    public string? Expected { get; init; }

    /// <summary>What was actually observed.</summary>
    [JsonPropertyName("actual")]
    public string? Actual { get; init; }

    /// <summary>The 'because' reason provided.</summary>
    [JsonPropertyName("because")]
    public string? Because { get; init; }

    /// <summary>Suggestions for fixing the failure.</summary>
    [JsonPropertyName("suggestions")]
    public List<string>? Suggestions { get; init; }
}

#endregion

/// <summary>
/// Token usage information.
/// </summary>
public class TokenUsageData
{
    /// <summary>Input/prompt tokens.</summary>
    [JsonPropertyName("inputTokens")]
    public int InputTokens { get; init; }

    /// <summary>Output/completion tokens.</summary>
    [JsonPropertyName("outputTokens")]
    public int OutputTokens { get; init; }

    /// <summary>Total tokens.</summary>
    [JsonPropertyName("totalTokens")]
    public int TotalTokens => InputTokens + OutputTokens;

    /// <summary>Estimated cost in USD.</summary>
    [JsonPropertyName("estimatedCost")]
    public decimal? EstimatedCost { get; init; }
}

/// <summary>
/// Summary of the entire execution.
/// </summary>
public class ExecutionSummary
{
    /// <summary>Whether all assertions passed.</summary>
    [JsonPropertyName("passed")]
    public bool Passed { get; init; }

    /// <summary>Total execution duration.</summary>
    [JsonPropertyName("totalDuration")]
    public TimeSpan TotalDuration { get; init; }

    /// <summary>Time to first token (for streaming).</summary>
    [JsonPropertyName("timeToFirstToken")]
    public TimeSpan? TimeToFirstToken { get; init; }

    /// <summary>Total number of execution steps.</summary>
    [JsonPropertyName("totalSteps")]
    public int TotalSteps { get; init; }

    /// <summary>Number of tool calls made.</summary>
    [JsonPropertyName("toolCallCount")]
    public int ToolCallCount { get; init; }

    /// <summary>Number of tool calls that failed.</summary>
    [JsonPropertyName("toolErrorCount")]
    public int ToolErrorCount { get; init; }

    /// <summary>Number of LLM requests made.</summary>
    [JsonPropertyName("llmRequestCount")]
    public int LlmRequestCount { get; init; }

    /// <summary>Total token usage across all requests.</summary>
    [JsonPropertyName("totalTokenUsage")]
    public TokenUsageData? TotalTokenUsage { get; init; }

    /// <summary>Total estimated cost.</summary>
    [JsonPropertyName("totalEstimatedCost")]
    public decimal? TotalEstimatedCost { get; init; }

    /// <summary>Summary of all assertions.</summary>
    [JsonPropertyName("assertions")]
    public List<AssertionSummary>? Assertions { get; init; }
}

/// <summary>
/// Summary of a single assertion result.
/// </summary>
public class AssertionSummary
{
    /// <summary>Assertion name/description.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Whether it passed.</summary>
    [JsonPropertyName("passed")]
    public bool Passed { get; init; }

    /// <summary>Message if failed.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}
