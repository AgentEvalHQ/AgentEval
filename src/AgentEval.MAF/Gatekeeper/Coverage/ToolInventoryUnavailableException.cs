// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Thrown by <see cref="GatekeeperCoverageAnalyzer.AnalyzeOrThrow(Microsoft.Agents.AI.AIAgent, IReadOnlyList{IToolGate}?, AnalyzeOptions?)"/>
/// when the tool inventory could not be read off the agent at all — see
/// <see cref="GatekeeperCoverageReport.ToolInventoryAvailable"/>. This happens for a custom
/// <see cref="Microsoft.Agents.AI.AIAgent"/> subclass that doesn't forward <c>GetService(typeof(ChatOptions))</c>
/// (e.g. does not derive from / wrap <c>ChatClientAgent</c>), and for an agent whose tools come only from a dynamic
/// tool provider (detected, or declared through <see cref="AnalyzeOptions.HasDynamicToolProvider"/>) with no static
/// tools.
/// <para><b>Why this must throw, not silently pass:</b> <c>AnalyzeOrThrow</c> exists specifically so a caller can
/// refuse to start an agent whose high-risk tools might be unprotected. "I could not verify coverage" and "I
/// verified coverage and it's bad" must fail the SAME direction — an empty, unreadable tool list would otherwise
/// make <see cref="GatekeeperCoverageReport.HasUnprotectedHighRiskTools"/> vacuously <see langword="false"/>,
/// silently reporting a clean bill of health for an agent that was never actually checked.</para>
/// </summary>
public sealed class ToolInventoryUnavailableException : Exception
{
    /// <summary>The (necessarily empty-tools) report that triggered this exception.</summary>
    public GatekeeperCoverageReport Report { get; }

    /// <summary>Creates the exception from the offending <paramref name="report"/>.</summary>
    public ToolInventoryUnavailableException(GatekeeperCoverageReport report)
        : base(BuildMessage(report))
    {
        ArgumentNullException.ThrowIfNull(report);
        Report = report;
    }

    // Name the cause the analyzer actually found: an agent that does not expose ChatOptions, or a dynamic tool
    // provider with no static tools (the second used to be reported as the first).
    private static string BuildMessage(GatekeeperCoverageReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.DynamicToolsNotInventoried
            ? "Gatekeeper coverage check failed: the tool inventory could not be listed (ToolInventoryAvailable=false — " +
              "a dynamic tool provider, e.g. an AIContextProvider, is present and no static tools are declared, so the " +
              "tools it injects at invocation time cannot be enumerated). AnalyzeOrThrow cannot verify whether any " +
              "high-risk tool is unprotected and refuses to assume it is safe."
            : "Gatekeeper coverage check failed: the tool inventory could not be read off this agent " +
              "(ToolInventoryAvailable=false — it does not expose ChatOptions via GetService, e.g. a custom, " +
              "non-ChatClientAgent AIAgent type). AnalyzeOrThrow cannot verify whether any high-risk tool is " +
              "unprotected and refuses to assume it is safe. Use the Analyze(IEnumerable<AITool>, ...) / " +
              "AnalyzeOrThrow(IEnumerable<AITool>, ...) overload with an explicit tool list instead.";
    }
}
