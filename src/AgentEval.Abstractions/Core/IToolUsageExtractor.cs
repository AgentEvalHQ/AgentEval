// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Models;

namespace AgentEval.Core;

/// <summary>
/// Interface for extracting tool usage information from agent responses.
/// Enables testability and dependency injection for tool extraction logic.
/// </summary>
/// <remarks>
/// Implementations should be stateless and thread-safe to support concurrent extraction operations.
/// This interface supports multiple extraction sources (raw messages or structured responses)
/// to provide flexibility in how tool usage data is accessed.
/// </remarks>
public interface IToolUsageExtractor
{
    /// <summary>
    /// Extracts tool usage report from raw chat messages.
    /// Analyzes function call contents and function result contents to build a complete usage report.
    /// </summary>
    /// <param name="rawMessages">The raw messages from an agent response. Can be null or empty.</param>
    /// <returns>
    /// A tool usage report containing all tool calls with their arguments and results.
    /// Returns an empty report if rawMessages is null or contains no tool calls.
    /// </returns>
    /// <remarks>
    /// This method searches for FunctionCallContent and FunctionResultContent in ChatMessage objects
    /// and correlates them by CallId to build a complete picture of tool usage.
    /// </remarks>
    ToolUsageReport Extract(IReadOnlyList<object>? rawMessages);
    
    /// <summary>
    /// Extracts tool usage report from a structured agent response.
    /// Convenience method that delegates to <see cref="Extract(IReadOnlyList{object}?)"/>.
    /// </summary>
    /// <param name="response">The agent response containing raw messages. Cannot be null.</param>
    /// <returns>A tool usage report containing all tool calls with their arguments and results.</returns>
    /// <exception cref="ArgumentNullException">Thrown when response is null.</exception>
    ToolUsageReport Extract(AgentResponse response);
}
