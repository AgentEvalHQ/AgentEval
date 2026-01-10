// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Models;

namespace AgentEval.Core;

/// <summary>
/// Default implementation of IToolUsageExtractor that delegates to ToolUsageExtractor static methods.
/// This allows dependency injection while maintaining backward compatibility with existing static usage.
/// </summary>
public class DefaultToolUsageExtractor : IToolUsageExtractor
{
    /// <summary>
    /// Singleton instance for use in dependency injection.
    /// </summary>
    public static IToolUsageExtractor Instance { get; } = new DefaultToolUsageExtractor();

    /// <inheritdoc />
    public ToolUsageReport Extract(IReadOnlyList<object>? rawMessages) 
        => ToolUsageExtractor.Extract(rawMessages);

    /// <inheritdoc />
    public ToolUsageReport Extract(AgentResponse response) 
        => ToolUsageExtractor.Extract(response);
}
