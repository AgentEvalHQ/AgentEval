// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Models;

namespace AgentEval.Assertions;

/// <summary>
/// Extension methods to start fluent assertions on model types.
/// </summary>
/// <remarks>
/// These methods were moved from instance methods on the model classes
/// to extension methods to decouple Models/ from Assertions/ (Phase 0.3).
/// Consumer code continues to work unchanged — <c>result.Performance!.Should()</c>
/// resolves the extension method when <c>using AgentEval.Assertions;</c> is in scope.
/// </remarks>
public static class ModelAssertionExtensions
{
    /// <summary>Start fluent assertions on performance metrics.</summary>
    public static PerformanceAssertions Should(this PerformanceMetrics metrics) => new(metrics);

    /// <summary>Start fluent assertions on a tool usage report.</summary>
    public static ToolUsageAssertions Should(this ToolUsageReport report) => new(report);
}
