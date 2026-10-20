// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;

namespace AgentEval.Samples;

/// <summary>
/// Whether a metric's result measured anything. Several Core metrics report a judge reply they could not parse, or an
/// embedding call that failed, as <c>MetricResult.Fail</c> with score 0, which reads as a measured failure: on a planted
/// bad case it even reads as a correct detection. The samples B1, B2 and B4 tell those apart here, in one place.
/// </summary>
/// <remarks>
/// Core marks the case in different ways, so all are checked: <see cref="MetricResult.Measured"/>; a
/// <c>evaluationStatus</c> detail of <c>inconclusive</c> or <c>parse_error</c> (bias, misinformation); a
/// <c>detectionMethod</c> of <c>llm_parse_error</c> (toxicity); an explanation starting "Unable to evaluate"
/// (groundedness, coherence, fluency) or naming a failed embedding call. Until Core reports these as not measured
/// itself, a reworded Core message would make one read as measured again; the tests of these samples' labels pin it.
/// </remarks>
internal static class MetricMeasurement
{
    private static readonly string[] NotMeasuredStatuses = ["inconclusive", "parse_error"];

    private static readonly string[] NotMeasuredExplanations =
        ["Unable to evaluate", "Embedding generation failed", "Failed to generate embeddings"];

    /// <summary>True when <paramref name="result"/> did not measure what the metric measures.</summary>
    public static bool CouldNotMeasure(MetricResult result)
    {
        if (!result.Measured)
            return true;
        if (result.Passed)
            return false;
        if (result.Details is { } details)
        {
            if (details.TryGetValue("evaluationStatus", out var status)
                && NotMeasuredStatuses.Contains(status?.ToString(), StringComparer.OrdinalIgnoreCase))
                return true;
            if (details.TryGetValue("detectionMethod", out var method)
                && string.Equals(method?.ToString(), "llm_parse_error", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return result.Explanation is { } why
               && NotMeasuredExplanations.Any(prefix => why.StartsWith(prefix, StringComparison.Ordinal));
    }
}
