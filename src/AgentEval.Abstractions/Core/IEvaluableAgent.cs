// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Core;

/// <summary>
/// Represents an evaluable AI agent abstraction.
/// Implement this interface to adapt any agent framework for evaluation.
/// </summary>
/// <remarks>
/// This follows the Interface Segregation Principle - only the minimal
/// methods needed for evaluation are required.
/// </remarks>
public interface IEvaluableAgent
{
    /// <summary>
    /// Gets the name of the agent for identification in reports.
    /// </summary>
    string Name { get; }
    
    /// <summary>
    /// Invokes the agent with a prompt and returns the response.
    /// </summary>
    /// <param name="prompt">The user prompt to send to the agent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The agent's response.</returns>
    Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default);
}

/// <summary>
/// Extended interface for agents that support streaming.
/// </summary>
public interface IStreamableAgent : IEvaluableAgent
{
    /// <summary>
    /// Invokes the agent with streaming response.
    /// </summary>
    /// <param name="prompt">The user prompt to send to the agent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Async enumerable of response chunks.</returns>
    IAsyncEnumerable<AgentResponseChunk> InvokeStreamingAsync(
        string prompt, 
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Response from an agent invocation.
/// </summary>
public class AgentResponse
{
    /// <summary>The text content of the response.</summary>
    public required string Text { get; init; }
    
    /// <summary>Raw messages from the underlying framework (for tool extraction).</summary>
    public IReadOnlyList<object>? RawMessages { get; init; }
    
    /// <summary>Token usage if available.</summary>
    public TokenUsage? TokenUsage { get; init; }
    
    /// <summary>Model used for the response.</summary>
    public string? ModelId { get; init; }

    /// <summary>Finish reason from the underlying framework (e.g., "Stop", "MaxTokens").</summary>
    public string? FinishReason { get; init; }

    /// <summary>Additional properties from the underlying framework response.</summary>
    public IReadOnlyDictionary<string, object?>? AdditionalProperties { get; init; }

    /// <summary>Ordered passages actually retrieved for this invocation; null means not captured, empty means none retrieved.</summary>
    /// <remarks>Supply this explicitly in the agent adapter; the harness does not infer retrieval from arbitrary tool results.</remarks>
    public IReadOnlyList<string>? RetrievedContext { get; init; }
}

/// <summary>
/// A chunk of a streaming response.
/// </summary>
public class AgentResponseChunk
{
    /// <summary>Text content in this chunk.</summary>
    public string? Text { get; init; }
    
    /// <summary>Tool call started in this chunk.</summary>
    public ToolCallInfo? ToolCallStarted { get; init; }
    
    /// <summary>Tool call completed in this chunk.</summary>
    public ToolResultInfo? ToolCallCompleted { get; init; }
    
    /// <summary>Whether this is the final chunk.</summary>
    public bool IsComplete { get; init; }
    
    /// <summary>
    /// Token usage information (typically only populated on the final/complete chunk).
    /// </summary>
    public TokenUsage? Usage { get; init; }
    
    /// <summary>
    /// Model used for the response (typically only populated on the final/complete chunk).
    /// </summary>
    public string? ModelId { get; init; }

    /// <summary>Invocation-owned retrieval on the complete chunk; null means unavailable, empty means none retrieved.</summary>
    /// <remarks>Only read when IsComplete is true. This is the full ordered collection, not a streaming delta.</remarks>
    public IReadOnlyList<string>? RetrievedContext { get; init; }
}

/// <summary>
/// Information about a tool call.
/// </summary>
public class ToolCallInfo
{
    public required string Name { get; init; }
    public required string CallId { get; init; }
    public IDictionary<string, object?>? Arguments { get; init; }
}

/// <summary>
/// Information about a tool result.
/// </summary>
public class ToolResultInfo
{
    public required string CallId { get; init; }
    public object? Result { get; init; }
    public Exception? Exception { get; init; }
}

/// <summary>
/// Token usage information.
/// </summary>
public class TokenUsage
{
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    /// <summary>Lossless provider input count when available; legacy integer counts may be saturated.</summary>
    public long? ReportedInputTokenCount { get; init; }
    /// <summary>Lossless provider output count when available; legacy integer counts may be saturated.</summary>
    public long? ReportedOutputTokenCount { get; init; }
    /// <summary>Cached subset of input tokens. Null means unreported; zero means explicitly none.</summary>
    public long? CachedInputTokenCount { get; init; }
    /// <summary>True when an aggregate contains multiple model identities without per-model token attribution.</summary>
    public bool HasMixedModelUsage { get; init; }
    public int TotalTokens => (int)Math.Min((long)PromptTokens + CompletionTokens, int.MaxValue);
    
    /// <summary>Alias for PromptTokens for consistency.</summary>
    public int InputTokens => PromptTokens;
    
    /// <summary>Alias for CompletionTokens for consistency.</summary>
    public int OutputTokens => CompletionTokens;
}
