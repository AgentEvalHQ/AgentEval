// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Models;

namespace AgentEval.Assertions;

/// <summary>
/// Extension methods to start fluent assertions on model types.
/// </summary>
/// <remarks>
/// These are extension methods that decouple Models/ from Assertions/.
/// Usage: <c>result.Performance!.Should()</c> — requires <c>using AgentEval.Assertions;</c> in scope.
/// </remarks>
public static class ModelAssertionExtensions
{
    /// <summary>Start fluent assertions on performance metrics.</summary>
    public static PerformanceAssertions Should(this PerformanceMetrics metrics) => new(metrics);

    /// <summary>Start fluent assertions on a tool usage report.</summary>
    /// <remarks>
    /// A <see langword="null"/> report means AgentEval has no record of the agent's tool calls, so no tool assertion
    /// can hold: it fails with a <see cref="ToolAssertionException"/> that says why, rather than passing or being
    /// skipped. An agent that cannot call tools fails the same way a tool assertion would on an empty report.
    /// </remarks>
    /// <exception cref="ToolAssertionException"><paramref name="report"/> is <see langword="null"/>.</exception>
    public static ToolUsageAssertions Should(this ToolUsageReport? report) =>
        report is null ? throw NoToolData() : new(report);

    private static ToolAssertionException NoToolData() => ToolAssertionException.Create(
        "There is no tool-call data to assert on: the result's ToolUsage is null.",
        expected: "A tool usage report (empty when the agent called no tools)",
        actual: "No report",
        suggestions:
        [
            "The agent's adapter must return the conversation in AgentResponse.RawMessages, or AgentEval cannot see " +
            "which tools were called. ChatClientAgentAdapter and the MAF adapter do; a custom IEvaluableAgent that " +
            "returns only text does not.",
            "Tool tracking must be on: EvaluationOptions.TrackTools = true (the default).",
        ]);
}
