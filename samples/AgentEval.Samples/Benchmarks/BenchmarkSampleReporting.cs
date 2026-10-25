// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Evals;
using AgentEval.Output;

namespace AgentEval.Samples.Benchmarks;

/// <summary>Projects benchmark results into persisted summaries and their console verdict.</summary>
internal static class BenchmarkSampleReporting
{
    /// <summary>
    /// Walks the <paramref name="node"/> EvalResult tree leaf-first, yielding
    /// each atomic leaf paired with its metric key. Mirrors
    /// <c>GdprBenchmarkRunner.EnumerateAtomicLeaves</c>.
    /// </summary>
    public static IEnumerable<(string Id, EvalResult Result)> EnumerateAtomicLeaves(EvalResult node)
    {
        var subs = node.Details.SubResults;
        if (subs is null || subs.Count == 0)
        {
            yield return (node.Metric.Key, node);
            yield break;
        }
        foreach (var child in subs)
            foreach (var leaf in EnumerateAtomicLeaves(child))
                yield return leaf;
    }

    /// <summary>
    /// Builds a <see cref="RunSummary"/> from a composite result's verdict and
    /// the rolled-up leaf statistics. Mirrors the shape the GDPR / OWASP CLI
    /// commands produce so Mission Control's view is identical.
    /// </summary>
    public static RunSummary BuildRunSummary(EvalResult root, string runId)
    {
        var stats = EnumerateAtomicLeaves(root).Select(l => l.Result.Score).ToRunStats();
        var metrics = new Dictionary<string, double>();
        if (root.Score.MeasuredValue() is { } value)
            metrics["overallScore"] = value;

        return new RunSummary(
            SchemaVersion: "1.0",
            RunId: runId,
            Verdict: root.Score.RunVerdict(stats),
            Stats: stats,
            Metrics: metrics);
    }

    public static string FormatVerdict(EvalResult result)
    {
        var label = string.Equals(result.Score.Label, "skipped", StringComparison.OrdinalIgnoreCase)
            ? "NOT TESTED"
            : result.Score.Label.ToUpperInvariant();
        var score = result.Score.MeasuredValue() is { } value ? value.ToString("P1") : "not measured";
        return $"   Verdict:  {label}   Score: {score}   Severity: {result.Score.Severity}";
    }
}
