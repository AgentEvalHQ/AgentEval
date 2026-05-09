// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MissionControl.GraphQL;

/// <summary>
/// Root GraphQL <c>Query</c> type for Mission Control.
/// </summary>
/// <remarks>
/// <para>
/// Plan-08 MC1.4.0 baseline scaffolding — only the smoke-test resolvers
/// (<see cref="Version"/> and <see cref="Ping"/>) ship in this commit.
/// </para>
/// <para>
/// Subsequent plan-08 tasks add the real resolvers:
/// </para>
/// <list type="bullet">
///   <item>MC1.4.2 — solution / subjects / runs / recentRuns (read via <c>IOutputStoreReader</c>)</item>
///   <item>MC1.4.3 — runCostBreakdown</item>
///   <item>MC1.4.4 — compliance + cross-regulation</item>
///   <item>MC1.4.5 — red-team campaigns</item>
///   <item>MC1.4.6 — recursive <c>EvalResult</c> resolver with depth limit</item>
///   <item>MC1.5.3 — evaluators (driven by the in-memory <c>EvaluatorCard</c> registry)</item>
/// </list>
/// </remarks>
public sealed class Query
{
    /// <summary>
    /// Returns the AgentEval assembly version currently serving this Mission Control instance.
    /// Useful for diagnostics and SPA version-skew detection.
    /// </summary>
    public string AgentEvalVersion() =>
        typeof(AgentEval.Output.IOutputStoreReader).Assembly
            .GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>
    /// Diagnostic resolver — returns the literal string <c>"pong"</c>. Used by
    /// <c>GraphQLSmokeTests</c> to verify the GraphQL endpoint accepts queries
    /// and returns well-formed responses.
    /// </summary>
    public string Ping() => "pong";
}
