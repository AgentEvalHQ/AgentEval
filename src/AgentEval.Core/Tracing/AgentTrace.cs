// SPDX-License-Identifier: MIT
// Copyright (c) 2025 AgentEval. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;
using AgentEval.Models;

namespace AgentEval.Tracing;

/// <summary>
/// Represents a complete recorded trace of agent execution.
/// This is the root object serialized to a .trace.json file.
/// </summary>
public sealed class AgentTrace
{
    /// <summary>
    /// Schema version for forward compatibility.
    /// </summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0";

    /// <summary>
    /// Human-readable name for this trace.
    /// </summary>
    [JsonPropertyName("traceName")]
    public string TraceName { get; set; } = string.Empty;

    /// <summary>
    /// When the trace was captured.
    /// </summary>
    [JsonPropertyName("capturedAt")]
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Name or identifier of the agent being traced.
    /// </summary>
    [JsonPropertyName("agentName")]
    public string? AgentName { get; set; }

    /// <summary>
    /// Model identifier used during recording.
    /// </summary>
    [JsonPropertyName("modelId")]
    public string? ModelId { get; set; }

    /// <summary>
    /// All recorded entries (requests and responses).
    /// </summary>
    [JsonPropertyName("entries")]
    public List<TraceEntry> Entries { get; set; } = new();

    /// <summary>
    /// Aggregate performance metrics for the entire trace.
    /// </summary>
    [JsonPropertyName("performance")]
    public TracePerformance? Performance { get; set; }

    /// <summary>
    /// Optional metadata stored with the trace.
    /// </summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, object>? Metadata { get; set; }
}

/// <summary>
/// A single entry in the trace (either a request or response).
/// </summary>
public class TraceEntry
{
    /// <summary>
    /// Type of entry: Request, Response, ToolCall, StreamChunk.
    /// </summary>
    [JsonPropertyName("type")]
    public TraceEntryType Type { get; set; }

    /// <summary>
    /// Zero-based index for matching (entry N in trace matches call N).
    /// </summary>
    [JsonPropertyName("index")]
    public int Index { get; set; }

    /// <summary>
    /// When this entry was recorded.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// The prompt text (for Request entries).
    /// </summary>
    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    /// <summary>
    /// The response text (for Response entries).
    /// </summary>
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    /// <summary>
    /// Duration in milliseconds (for Response entries).
    /// </summary>
    [JsonPropertyName("durationMs")]
    public long? DurationMs { get; set; }

    /// <summary>
    /// Tool calls made during this response.
    /// </summary>
    [JsonPropertyName("toolCalls")]
    public List<TraceToolCall>? ToolCalls { get; set; }

    /// <summary>
    /// Token usage for this response.
    /// </summary>
    [JsonPropertyName("tokenUsage")]
    public TraceTokenUsage? TokenUsage { get; set; }

    /// <summary>
    /// Streaming chunks (for StreamChunk entries or detailed Response).
    /// </summary>
    [JsonPropertyName("streamingChunks")]
    public List<TraceStreamChunk>? StreamingChunks { get; set; }

    /// <summary>
    /// Whether this was a streaming response.
    /// </summary>
    [JsonPropertyName("isStreaming")]
    public bool? IsStreaming { get; set; }

    /// <summary>
    /// Error information if the call failed.
    /// </summary>
    [JsonPropertyName("error")]
    public TraceError? Error { get; set; }
}

/// <summary>
/// Types of trace entries.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TraceEntryType
{
    /// <summary>A request sent to the agent.</summary>
    Request,

    /// <summary>A response received from the agent.</summary>
    Response,

    /// <summary>A tool call executed by the agent.</summary>
    ToolCall,

    /// <summary>A streaming chunk received.</summary>
    StreamChunk
}

/// <summary>
/// Recorded tool call information.
/// </summary>
public class TraceToolCall
{
    /// <summary>
    /// Name of the tool that was called.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Arguments passed to the tool (JSON string or object).
    /// </summary>
    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; }

    /// <summary>
    /// Result returned by the tool.
    /// </summary>
    [JsonPropertyName("result")]
    public string? Result { get; set; }

    /// <summary>
    /// When the tool call started.
    /// </summary>
    [JsonPropertyName("startedAt")]
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>
    /// Duration of the tool call in milliseconds.
    /// </summary>
    [JsonPropertyName("durationMs")]
    public long? DurationMs { get; set; }

    /// <summary>
    /// Whether the tool call succeeded.
    /// </summary>
    [JsonPropertyName("succeeded")]
    public bool Succeeded { get; set; } = true;

    /// <summary>
    /// Error message if the tool call failed.
    /// </summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// Token usage information.
/// </summary>
public class TraceTokenUsage
{
    /// <summary>
    /// Number of tokens in the prompt.
    /// </summary>
    [JsonPropertyName("promptTokens")]
    public int PromptTokens { get; set; }

    /// <summary>
    /// Number of tokens in the completion.
    /// </summary>
    [JsonPropertyName("completionTokens")]
    public int CompletionTokens { get; set; }

    /// <summary>
    /// Total tokens used.
    /// </summary>
    [JsonPropertyName("totalTokens")]
    public int TotalTokens => PromptTokens + CompletionTokens;
}

/// <summary>
/// A single streaming chunk.
/// </summary>
public class TraceStreamChunk
{
    /// <summary>
    /// Index of this chunk in the stream.
    /// </summary>
    [JsonPropertyName("index")]
    public int Index { get; set; }

    /// <summary>
    /// Text content of the chunk.
    /// </summary>
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    /// <summary>
    /// Delay from previous chunk in milliseconds (for replay timing).
    /// </summary>
    [JsonPropertyName("delayMs")]
    public int DelayMs { get; set; }

    /// <summary>
    /// Whether this is a tool call chunk.
    /// </summary>
    [JsonPropertyName("isToolCall")]
    public bool IsToolCall { get; set; }

    /// <summary>
    /// Tool call name if this is a tool call chunk.
    /// </summary>
    [JsonPropertyName("toolName")]
    public string? ToolName { get; set; }
}

/// <summary>
/// Error information.
/// </summary>
public class TraceError
{
    /// <summary>
    /// Error type or exception name.
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>
    /// Error message.
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>
    /// Stack trace (optional, may be sanitized).
    /// </summary>
    [JsonPropertyName("stackTrace")]
    public string? StackTrace { get; set; }
}

/// <summary>
/// Aggregate performance metrics for the entire trace.
/// </summary>
public class TracePerformance
{
    /// <summary>
    /// Total duration of all calls in milliseconds.
    /// </summary>
    [JsonPropertyName("totalDurationMs")]
    public long TotalDurationMs { get; set; }

    /// <summary>
    /// Total prompt tokens used.
    /// </summary>
    [JsonPropertyName("totalPromptTokens")]
    public int TotalPromptTokens { get; set; }

    /// <summary>
    /// Total completion tokens used.
    /// </summary>
    [JsonPropertyName("totalCompletionTokens")]
    public int TotalCompletionTokens { get; set; }

    /// <summary>
    /// Total tokens used.
    /// </summary>
    [JsonPropertyName("totalTokens")]
    public int TotalTokens => TotalPromptTokens + TotalCompletionTokens;

    /// <summary>
    /// Time to first token for the first streaming response (if any).
    /// </summary>
    [JsonPropertyName("timeToFirstTokenMs")]
    public long? TimeToFirstTokenMs { get; set; }

    /// <summary>
    /// Number of API calls made.
    /// </summary>
    [JsonPropertyName("callCount")]
    public int CallCount { get; set; }

    /// <summary>
    /// Number of tool calls made.
    /// </summary>
    [JsonPropertyName("toolCallCount")]
    public int ToolCallCount { get; set; }
}
