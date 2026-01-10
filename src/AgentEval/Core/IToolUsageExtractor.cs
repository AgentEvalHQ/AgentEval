// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Models;

namespace AgentEval.Core;

/// <summary>
/// Interface for extracting tool usage information from agent responses.
/// Enables testability and dependency injection for tool extraction logic.
/// </summary>
public interface IToolUsageExtractor
{
    /// <summary>
    /// Extract tool usage report from raw chat messages.
    /// </summary>
    /// <param name="rawMessages">The raw messages from an agent response.</param>
    /// <returns>A tool usage report containing all tool calls.</returns>
    ToolUsageReport Extract(IReadOnlyList<object>? rawMessages);
    
    /// <summary>
    /// Extract tool usage report from an agent response.
    /// </summary>
    /// <param name="response">The agent response.</param>
    /// <returns>A tool usage report containing all tool calls.</returns>
    ToolUsageReport Extract(AgentResponse response);
}
