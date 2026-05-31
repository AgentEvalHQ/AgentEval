// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Core;   // ToolUsageExtractor, AgentResponse
using AgentEval.Models; // ToolUsageReport, ToolCallRecord

namespace AgentEval.RedTeam;

/// <summary>
/// RC-1 shared tool-call extraction for red-team evaluators. Walks
/// <see cref="AgentResponse.RawMessages"/> for FunctionCallContent and pairs results by CallId.
/// </summary>
/// <remarks>
/// Reuses <see cref="ToolUsageExtractor"/> (AgentEval.Core) rather than re-lifting
/// <c>ConversationRunner.ExtractToolCalls</c>, which returns a framework-specific ToolCallInfo
/// record and does not pair tool results. Single source of truth for the
/// RawMessages → FunctionCallContent walk.
/// </remarks>
public static class ProbeToolCalls
{
    private static readonly ToolUsageReport Empty = new();

    /// <summary>Extract the tool-usage report from an agent response (never null).</summary>
    public static ToolUsageReport Extract(AgentResponse? response)
        => response is null ? Empty : ToolUsageExtractor.Extract(response.RawMessages);

    /// <summary>Records for any tool whose name matches one of <paramref name="forbidden"/> (case-insensitive).</summary>
    public static IReadOnlyList<ToolCallRecord> ForbiddenCalls(AgentResponse? response, IEnumerable<string> forbidden)
    {
        ArgumentNullException.ThrowIfNull(forbidden);
        var report = Extract(response);
        if (report.Count == 0) return Array.Empty<ToolCallRecord>();
        var set = new HashSet<string>(forbidden, StringComparer.OrdinalIgnoreCase);
        return report.Calls.Where(c => set.Contains(c.Name)).ToList();
    }

    /// <summary>True if the response invoked at least one forbidden tool.</summary>
    public static bool InvokedForbiddenTool(AgentResponse? response, IEnumerable<string> forbidden)
        => ForbiddenCalls(response, forbidden).Count > 0;

    /// <summary>True if the response invoked any tool at all.</summary>
    public static bool InvokedAnyTool(AgentResponse? response)
        => Extract(response).Count > 0;
}
