// SPDX-License-Identifier: MIT
// Copyright (c) 2025 AgentEval. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;
using System.Threading;
using AgentEval.Models;

namespace AgentEval.Tracing;

/// <summary>
/// Represents a complete recorded trace of agent execution.
/// This is the root object serialized to a .trace.json file.
/// </summary>
public sealed class AgentTrace
{
    /// <summary>
    /// Schema version — the highest schema version of any content the trace may contain.
    /// "1.1" (Glass Box) means it may carry v1.1 fields (Scope, CorrelationId, ToolDefinitions,
    /// FinishReason, RequestOptions, ProviderMetadata); individual entries may still be v1.0-shaped.
    /// Consumers branch on per-entry data (e.g. <see cref="TraceEntry.EffectiveScope"/>), not this string.
    /// </summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.1";

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

    // Glass Box recorders may write from concurrent tool invocations (MEAI AllowConcurrentInvocation) or
    // from parallel chat calls that share one trace; List<>/Dictionary<> are not thread-safe, so funnel all
    // mutating writes through these guarded helpers. Reads (serialization, FinalizePerformance) run after
    // recording has completed, so they need no lock.
    private readonly object _writeLock = new();
    private int _toolExecutionIndex = -1;

    /// <summary>Appends an entry to <see cref="Entries"/> under a lock — safe under concurrent recording.</summary>
    public void AddEntry(TraceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_writeLock)
        {
            Entries.Add(entry);
        }
    }

    /// <summary>
    /// Atomically allocates the next <see cref="TraceEntryScope.ToolExecution"/> index (14). Multiple
    /// <c>EvaluatingAIFunction</c>-wrapped tools sharing this trace can execute concurrently (MEAI
    /// AllowConcurrentInvocation); reading a would-be index off <see cref="Entries"/>.Count without
    /// synchronization is a TOCTOU race that can hand two concurrent tool calls the same index. Mirrors
    /// <c>TraceRecordingChatClient</c>'s own <c>Interlocked</c>-incremented per-scope counter.
    /// </summary>
    public int NextToolExecutionIndex() => Interlocked.Increment(ref _toolExecutionIndex);

    /// <summary>Sets a <see cref="Metadata"/> key under a lock, lazily creating the dictionary — safe under concurrent recording.</summary>
    public void SetMetadata(string key, object value)
    {
        lock (_writeLock)
        {
            Metadata ??= new Dictionary<string, object>();
            Metadata[key] = value;
        }
    }
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

    // ── AgentTrace v1.1 (Glass Box) additive fields — all nullable; absent ⇒ v1.0 semantics. ──

    /// <summary>
    /// Recording layer this entry was captured at (Glass Box, v1.1). Null on v1.0 traces and on
    /// agent-boundary entries — interpret null as <see cref="TraceEntryScope.AgentInvocation"/>.
    /// </summary>
    [JsonPropertyName("scope")]
    public TraceEntryScope? Scope { get; set; }

    /// <summary>
    /// Per-invocation correlation id (v1.1). Stamped identically on every ChatTurn and ToolExecution
    /// entry produced during one agent invocation so consumers can group an invocation's evidence and
    /// link tool executions to the invocation that triggered them. Not a pairing key — see <see cref="Index"/>.
    /// </summary>
    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }

    /// <summary>Verbatim system prompt sent to the model on this turn (v1.1, ChatTurn requests).</summary>
    [JsonPropertyName("systemPrompt")]
    public string? SystemPrompt { get; set; }

    /// <summary>Tool definitions advertised to the model on this turn (v1.1, ChatTurn requests).</summary>
    [JsonPropertyName("toolDefinitions")]
    public List<TraceToolDefinition>? ToolDefinitions { get; set; }

    /// <summary>Provider finish reason for this turn, e.g. "stop"/"tool_calls"/"length"/"content_filter" (v1.1).</summary>
    [JsonPropertyName("finishReason")]
    public string? FinishReason { get; set; }

    /// <summary>Sampling / request options captured per turn (v1.1): temperature, maxOutputTokens, responseFormat, modelId.</summary>
    [JsonPropertyName("requestOptions")]
    public Dictionary<string, object?>? RequestOptions { get; set; }

    /// <summary>Provider-specific metadata (e.g. Azure content-filter verdicts) (v1.1).</summary>
    [JsonPropertyName("providerMetadata")]
    public Dictionary<string, object?>? ProviderMetadata { get; set; }

    /// <summary>Convenience: the effective scope, treating null as <see cref="TraceEntryScope.AgentInvocation"/>. Not serialized.</summary>
    [JsonIgnore]
    public TraceEntryScope EffectiveScope => Scope ?? TraceEntryScope.AgentInvocation;

    // ── v1.1 factory helpers — return a populated base TraceEntry (serialization-safe; no subclassing). ──

    /// <summary>Builds a ChatTurn request entry from the messages and options sent to the model.</summary>
    public static TraceEntry ForChatRequest(
        int index, string? correlationId,
        string? systemPrompt, string? promptText,
        List<TraceToolDefinition>? toolDefinitions,
        Dictionary<string, object?>? requestOptions) => new()
    {
        Type = TraceEntryType.Request,
        Scope = TraceEntryScope.ChatTurn,
        Index = index,
        CorrelationId = correlationId,
        Timestamp = DateTimeOffset.UtcNow,
        SystemPrompt = systemPrompt,
        Prompt = promptText,
        ToolDefinitions = toolDefinitions,
        RequestOptions = requestOptions,
    };

    /// <summary>Builds a ChatTurn response entry from the model's reply.</summary>
    public static TraceEntry ForChatResponse(
        int index, string? correlationId, string? text, long durationMs,
        TraceTokenUsage? usage, List<TraceToolCall>? toolCalls,
        string? finishReason, Dictionary<string, object?>? providerMetadata) => new()
    {
        Type = TraceEntryType.Response,
        Scope = TraceEntryScope.ChatTurn,
        Index = index,
        CorrelationId = correlationId,
        Timestamp = DateTimeOffset.UtcNow,
        Text = text,
        DurationMs = durationMs,
        TokenUsage = usage,
        ToolCalls = toolCalls,
        FinishReason = finishReason,
        ProviderMetadata = providerMetadata,
    };

    /// <summary>Builds an error entry for a failed ChatTurn round-trip.</summary>
    public static TraceEntry ForChatError(int index, string? correlationId, Exception ex, long durationMs) => new()
    {
        Type = TraceEntryType.Response,
        Scope = TraceEntryScope.ChatTurn,
        Index = index,
        CorrelationId = correlationId,
        Timestamp = DateTimeOffset.UtcNow,
        DurationMs = durationMs,
        Error = new TraceError { Type = ex.GetType().Name, Message = ex.Message, StackTrace = ex.StackTrace },
    };

    /// <summary>Builds a ToolExecution entry (Glass Box Phase 2). Type is <see cref="TraceEntryType.ToolCall"/>.</summary>
    public static TraceEntry ForToolExecution(
        int index, string? correlationId, string toolName, string? arguments,
        string? result, long durationMs, bool succeeded, string? error) => new()
    {
        Type = TraceEntryType.ToolCall,
        Scope = TraceEntryScope.ToolExecution,
        Index = index,
        CorrelationId = correlationId,
        Timestamp = DateTimeOffset.UtcNow,
        DurationMs = durationMs,
        ToolCalls = new List<TraceToolCall>
        {
            new() { Name = toolName, Arguments = arguments, Result = result, DurationMs = durationMs, Succeeded = succeeded, Error = error },
        },
    };
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
/// The recording layer an entry was captured at (Glass Box, AgentTrace v1.1).
/// Declared after <see cref="TraceEntryType"/>; <see cref="AgentInvocation"/> = 0 preserves v1.0 semantics
/// when the field is absent/null.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TraceEntryScope
{
    /// <summary>Agent-boundary entry (TraceRecordingAgent). v1.0 default semantics.</summary>
    AgentInvocation = 0,

    /// <summary>One LLM round-trip at the IChatClient boundary (TraceRecordingChatClient).</summary>
    ChatTurn = 1,

    /// <summary>One tool/function execution (EvaluatingAIFunction).</summary>
    ToolExecution = 2,
}

/// <summary>
/// A tool/function definition as advertised to the model on a chat turn (AgentTrace v1.1).
/// </summary>
public class TraceToolDefinition
{
    /// <summary>Tool name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Human-readable tool description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>JSON schema of the parameters, as a JSON string (verbatim). Null when de-duplicated (Smoke/Standard presets).</summary>
    [JsonPropertyName("parametersSchema")]
    public string? ParametersSchema { get; set; }
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
