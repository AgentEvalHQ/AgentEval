// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Models;

namespace AgentEval.Core;

/// <summary>
/// Default implementation of <see cref="IToolUsageExtractor"/> that delegates to <see cref="ToolUsageExtractor"/> static methods.
/// This adapter allows dependency injection while maintaining backward compatibility with existing static usage.
/// </summary>
/// <remarks>
/// This implementation is stateless and thread-safe. The singleton instance can be shared across
/// the application. For custom extraction logic, implement <see cref="IToolUsageExtractor"/> directly.
/// </remarks>
public sealed class DefaultToolUsageExtractor : IToolUsageExtractor
{
    /// <summary>
    /// Singleton instance for use in dependency injection.
    /// Using a singleton is safe because the implementation is stateless.
    /// </summary>
    public static IToolUsageExtractor Instance { get; } = new DefaultToolUsageExtractor();

    /// <summary>
    /// Public constructor for dependency injection container.
    /// For most scenarios, prefer using the <see cref="Instance"/> singleton.
    /// </summary>
    public DefaultToolUsageExtractor() { }

    /// <inheritdoc />
    public ToolUsageReport Extract(IReadOnlyList<object>? rawMessages) 
        => ToolUsageExtractor.Extract(rawMessages);

    /// <inheritdoc />
    public ToolUsageReport Extract(AgentResponse response) 
        => ToolUsageExtractor.Extract(response);
}
