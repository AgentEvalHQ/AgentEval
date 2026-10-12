// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Core;

/// <summary>Records observed grading without changing a metric's existing outcome.</summary>
internal static class MetricInstrument
{
    // Callers supply the actual prompt builder with data slots empty or fixed placeholders, never case data.
    internal static MetricResult Model(MetricResult result, string template) => Copy(result, true,
        "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(template.Replace("\r\n", "\n", StringComparison.Ordinal))))
            .ToLowerInvariant());

    internal static MetricResult Code(MetricResult result) => Copy(result, false, null);

    private static MetricResult Copy(MetricResult result, bool modelGraded, string? digest) => new()
    {
        MetricName = result.MetricName, Score = result.Score, Passed = result.Passed,
        Measured = result.Measured, Explanation = result.Explanation, Details = result.Details,
        ModelGraded = modelGraded, RubricDigest = digest,
    };
}
