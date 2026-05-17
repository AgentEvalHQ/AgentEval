// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals.Agentic.Telemetry;

/// <summary>
/// Internal helpers shared by all telemetry evaluators.
/// </summary>
internal static class TelemetryHelper
{
    /// <summary>
    /// Resolves <see cref="AgenticTelemetry"/> from input metadata (takes precedence) or from
    /// the constructor-supplied fallback. Returns <c>null</c> if neither is available.
    /// </summary>
    internal static AgenticTelemetry? Resolve(EvalInput input, AgenticTelemetry? fallback)
    {
        if (input.Metadata is not null &&
            input.Metadata.TryGetValue(LatencyEval.MetadataKey, out var raw) &&
            raw is AgenticTelemetry fromMeta)
        {
            return fromMeta;
        }

        return fallback;
    }

    /// <summary>
    /// Computes a linear score:
    /// <list type="bullet">
    ///   <item>1.0 when <paramref name="actual"/> &lt;= <paramref name="threshold"/></item>
    ///   <item>0.0 when <paramref name="actual"/> &gt;= 2 × <paramref name="threshold"/></item>
    ///   <item>Linear interpolation between threshold and 2 × threshold.</item>
    /// </list>
    /// </summary>
    internal static double LinearScore(double actual, double threshold)
    {
        if (threshold <= 0) return actual <= 0 ? 1.0 : 0.0;
        if (actual <= threshold) return 1.0;
        if (actual >= 2.0 * threshold) return 0.0;
        // Linear between threshold and 2*threshold → score in (0, 1)
        return 1.0 - (actual - threshold) / threshold;
    }
}
