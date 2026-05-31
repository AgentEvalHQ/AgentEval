// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Benchmarks;

/// <summary>
/// The Trace Fidelity scoring rubric (Glass Box). Severity-weighted, fully specified, and pinned by a
/// divergence test. Six discrepancy classes; per-class child score and a severity-weighted root score, all 0–1.
/// </summary>
public static class TraceFidelityRubric
{
    /// <summary>Model emitted a tool call the agent's account omitted.</summary>
    public const string MissingToolCalls = "missing_tool_calls";

    /// <summary>Agent reports a call the model never requested.</summary>
    public const string PhantomToolCalls = "phantom_tool_calls";

    /// <summary>Same tool, different args between the two layers.</summary>
    public const string ArgumentDrift = "argument_drift";

    /// <summary>Chat boundary saw more round-trips for a tool than the agent reported (silent retries).</summary>
    public const string HiddenRetries = "hidden_retries";

    /// <summary>Per-turn token sums disagree with the agent-layer totals.</summary>
    public const string TokenUnderReporting = "token_under_reporting";

    /// <summary>Chat boundary saw content_filter/length; agent boundary reported stop/none.</summary>
    public const string SuppressedFinishReason = "suppressed_finish_reason";

    /// <summary>All six discrepancy classes, in canonical order.</summary>
    public static readonly IReadOnlyList<string> Classes = new[]
    {
        MissingToolCalls, PhantomToolCalls, ArgumentDrift, HiddenRetries, TokenUnderReporting, SuppressedFinishReason,
    };

    /// <summary>Token total comparison tolerance (2%).</summary>
    public const double TokenToleranceFraction = 0.02;

    /// <summary>Severity label for a discrepancy class.</summary>
    public static string Severity(string classKey) => classKey switch
    {
        SuppressedFinishReason => "Critical",
        MissingToolCalls or PhantomToolCalls or HiddenRetries => "High",
        ArgumentDrift => "Medium",
        TokenUnderReporting => "Low",
        _ => "Low",
    };

    /// <summary>Family weight for a discrepancy class. The six weights sum to 1.00.</summary>
    public static double Weight(string classKey) => classKey switch
    {
        MissingToolCalls => 0.20,
        PhantomToolCalls => 0.20,
        HiddenRetries => 0.20,
        SuppressedFinishReason => 0.15,
        ArgumentDrift => 0.15,
        TokenUnderReporting => 0.10,
        _ => 0.0,
    };

    /// <summary>Per-occurrence penalty for a severity label.</summary>
    public static double SeverityPenalty(string severity) => severity switch
    {
        "Critical" => 1.00,
        "High" => 0.50,
        "Medium" => 0.25,
        "Low" => 0.10,
        _ => 0.10,
    };

    /// <summary>Per-class child score: <c>1 - min(severityPenalty × count, 1)</c>, clamped to [0,1].</summary>
    public static double ChildValue(string classKey, int count)
        => Math.Clamp(1.0 - Math.Min(SeverityPenalty(Severity(classKey)) * count, 1.0), 0.0, 1.0);
}
