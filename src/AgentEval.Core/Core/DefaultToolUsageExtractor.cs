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
/// <para>
/// This implementation is stateless and thread-safe. The singleton instance can be shared across
/// the application. For custom extraction logic, implement <see cref="IToolUsageExtractor"/> directly.
/// </para>
/// <para>
/// By default it is <b>blind to approval-gated tool calls</b> (MAF's <c>ToolApprovalRequestContent</c>) and,
/// when given a logger, <b>warns every time it sees and drops one</b> — a report that silently omits the
/// calls a human was asked to approve makes <c>NeverCallTool</c> unfailable (ADR-030 Slice 0.5). Construct
/// with <c>includeApprovalGatedCalls: true</c> to record them instead.
/// </para>
/// </remarks>
public sealed class DefaultToolUsageExtractor : IToolUsageExtractor
{
    private readonly IAgentEvalLogger? _logger;
    private readonly bool _includeApprovalGatedCalls;

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

    /// <summary>
    /// Creates the default (approval-blind) extractor with a logger that is warned whenever an
    /// approval-gated call is seen and dropped.
    /// </summary>
    /// <param name="logger">Receives a warning per extraction that dropped approval-gated calls.</param>
    public DefaultToolUsageExtractor(IAgentEvalLogger logger)
        : this(logger, includeApprovalGatedCalls: false)
    {
    }

    /// <summary>
    /// Creates an extractor that either records approval-gated calls (<paramref name="includeApprovalGatedCalls"/>
    /// <see langword="true"/>) or drops them and warns (<see langword="false"/>).
    /// </summary>
    /// <param name="logger">Optional logger; when set, a warning is logged for every extraction that dropped approval-gated calls.</param>
    /// <param name="includeApprovalGatedCalls">See <see cref="ToolUsageExtractor.Extract(IReadOnlyList{object}?, bool)"/>.</param>
    public DefaultToolUsageExtractor(IAgentEvalLogger? logger, bool includeApprovalGatedCalls)
    {
        _logger = logger;
        _includeApprovalGatedCalls = includeApprovalGatedCalls;
    }

    /// <summary>Whether this extractor records approval-gated calls.</summary>
    public bool IncludeApprovalGatedCalls => _includeApprovalGatedCalls;

    /// <inheritdoc />
    public ToolUsageReport Extract(IReadOnlyList<object>? rawMessages)
    {
        var report = ToolUsageExtractor.Extract(rawMessages, _includeApprovalGatedCalls);
        WarnIfDropped(report, rawMessages);
        return report;
    }

    /// <inheritdoc />
    public ToolUsageReport Extract(AgentResponse response)
    {
        _ = response ?? throw new ArgumentNullException(nameof(response));
        return Extract(response.RawMessages);
    }

    private void WarnIfDropped(ToolUsageReport report, IReadOnlyList<object>? rawMessages)
    {
        if (_logger is null || report.DroppedApprovalRequestCount == 0) return;
        _logger.LogWarning(FormatDropWarning(report.DroppedApprovalRequestCount, ToolUsageExtractor.ApprovalGatedToolNames(rawMessages)));
    }

    /// <summary>
    /// The one warning text used wherever the default extraction drops an approval-gated call, so the
    /// message is the same in the harness and here.
    /// </summary>
    /// <param name="droppedCount">How many were dropped.</param>
    /// <param name="toolNames">The gated tool names, when known.</param>
    /// <param name="optInHint">How the caller opts in (defaults to the extractor overload).</param>
    public static string FormatDropWarning(int droppedCount, IReadOnlyList<string> toolNames, string? optInHint = null)
    {
        var names = toolNames.Count > 0 ? $" [{string.Join(", ", toolNames)}]" : string.Empty;
        return $"⚠️ Tool usage is INCOMPLETE: {droppedCount} approval-gated tool call(s){names} were seen and NOT recorded. " +
               "MAF wraps an approval-required call in ToolApprovalRequestContent, which the default extractor does not read; " +
               "absence-based policies (NeverCallTool) evaluated on this report cannot fail. " +
               $"Opt in with {optInHint ?? "ToolUsageExtractor.Extract(rawMessages, includeApprovalGatedCalls: true)"} to record them (ADR-030 Slice 0.5).";
    }
}
