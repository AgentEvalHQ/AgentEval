// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;

namespace AgentEval.Evals.Agentic.JudgeQuality;

/// <summary>
/// Meta-evaluator that compares two evaluation snapshots (e.g. two run IDs) and computes
/// a drift metric: the maximum absolute delta between corresponding per-evaluator scores.
/// Lower drift = higher score = better.
/// Pure-code — no LLM invocation.
/// <para>
/// <b>Input contract</b> (<see cref="EvalInput.Metadata"/> keys):
/// <list type="bullet">
///   <item>
///     <c>"snapshot_a"</c> (required) — first snapshot as a
///     <see cref="IReadOnlyDictionary{String,Double}"/> of <c>key → score</c> entries,
///     or a JSON string of an object with string keys and numeric values.
///   </item>
///   <item>
///     <c>"snapshot_b"</c> (required) — second snapshot in the same format.
///   </item>
/// </list>
/// </para>
/// <para>
/// <b>Score semantics</b>: <c>score = 1.0 - max_delta</c> (clamped to [0, 1]).
/// <c>max_delta</c> is the maximum <c>|scoreA - scoreB|</c> across all evaluator keys
/// that appear in both snapshots.
/// Passes when <c>score &gt;= (1.0 - passThreshold)</c>, i.e. when max_delta &lt; <c>passThreshold</c>.
/// Severity: <c>low</c> (meta-metric).
/// </para>
/// <para>
/// <b>Example</b>: if both snapshots have the same 3 evaluator scores, max_delta = 0,
/// score = 1.0, result = pass. If one evaluator drifted by 0.20, score = 0.80.
/// </para>
/// </summary>
public sealed class JudgeDriftEval : IEval
{
    private readonly double _passThreshold;

    /// <inheritdoc/>
    public string Key => "judge_drift";

    /// <inheritdoc/>
    public string Name => "Judge Drift";

    /// <inheritdoc/>
    public string Category => "judge-quality";

    /// <inheritdoc/>
    public string Version => "1.0.0";

    /// <summary>
    /// Initialises a new <see cref="JudgeDriftEval"/>.
    /// </summary>
    /// <param name="passThreshold">
    /// Maximum allowed drift (max delta) for the eval to pass. Defaults to 0.05 (5%).
    /// The final score is <c>1.0 - max_delta</c>, so a threshold of 0.05 means the eval
    /// passes when max_delta &lt; 0.05 (i.e. score &gt; 0.95).
    /// </param>
    public JudgeDriftEval(double passThreshold = 0.05)
    {
        if (passThreshold is < 0.0 or > 1.0)
            throw new ArgumentOutOfRangeException(
                nameof(passThreshold), passThreshold, "Pass threshold must be in [0, 1].");
        _passThreshold = passThreshold;
    }

    /// <inheritdoc/>
    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var (snapshotA, snapshotB) = ExtractSnapshots(input);

        if (snapshotA.Count == 0 || snapshotB.Count == 0)
        {
            return Task.FromResult(EvalResult.Skipped(this,
                "JudgeDriftEval requires two snapshots in EvalInput.Metadata[\"snapshot_a\"] and [\"snapshot_b\"]."));
        }

        // Find all keys present in both snapshots and compute max delta
        var sharedKeys = snapshotA.Keys
            .Intersect(snapshotB.Keys, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sharedKeys.Count == 0)
        {
            return Task.FromResult(EvalResult.Skipped(this,
                "JudgeDriftEval: snapshots share no common evaluator keys — cannot compute drift."));
        }

        var deltas = sharedKeys
            .Select(k => Math.Abs(snapshotA[k] - snapshotB[k]))
            .ToList();

        var maxDelta = deltas.Max();
        var score = Math.Max(0.0, Math.Min(1.0, 1.0 - maxDelta));

        // Pass when max_delta < passThreshold
        var passed = maxDelta < _passThreshold;
        var label = passed ? "pass" : "fail";
        var severity = passed ? "none" : "low";

        // The scoreThreshold to store is 1 - passThreshold (since score = 1 - max_delta)
        var scoreThreshold = 1.0 - _passThreshold;

        var dimensions = new Dictionary<string, double> { ["max_delta"] = maxDelta, ["shared_keys"] = sharedKeys.Count };
        for (int i = 0; i < sharedKeys.Count; i++)
            dimensions[$"delta_{sharedKeys[i]}"] = deltas[i];

        return Task.FromResult(new EvalResult(
            Metric: new(Key, Name, Category, Version),
            Score: new(score, null, label, passed, scoreThreshold, severity, null),
            Details: new(
                Dimensions: dimensions,
                Evidence: null,
                Recommendations: passed
                    ? null
                    : new[] { $"Judge drift detected (max_delta={maxDelta:F3} >= threshold={_passThreshold:F3}). Compare evaluator versions and re-calibrate." },
                SubResults: null,
                AggregationStrategy: "max_delta"),
            Provenance: new("code", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (IReadOnlyDictionary<string, double> A, IReadOnlyDictionary<string, double> B)
        ExtractSnapshots(EvalInput input)
    {
        var a = ExtractSnapshot(input, "snapshot_a");
        var b = ExtractSnapshot(input, "snapshot_b");
        return (a, b);
    }

    private static IReadOnlyDictionary<string, double> ExtractSnapshot(EvalInput input, string key)
    {
        if (input.Metadata is null || !input.Metadata.TryGetValue(key, out var raw))
            return new Dictionary<string, double>();

        return raw switch
        {
            IReadOnlyDictionary<string, double> typed => typed,
            IDictionary<string, double> dict => new Dictionary<string, double>(dict),
            string json => ParseJsonSnapshot(json),
            _ => TryConvertDynamic(raw)
        };
    }

    private static IReadOnlyDictionary<string, double> ParseJsonSnapshot(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new Dictionary<string, double>();

            return doc.RootElement
                .EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.Number)
                .ToDictionary(p => p.Name, p => p.Value.GetDouble());
        }
        catch (JsonException)
        {
            return new Dictionary<string, double>();
        }
    }

    private static IReadOnlyDictionary<string, double> TryConvertDynamic(object raw)
    {
        if (raw is global::System.Collections.IDictionary dict)
        {
            var result = new Dictionary<string, double>();
            foreach (var k in dict.Keys)
            {
                if (k is string sk && dict[k] is double dv)
                    result[sk] = dv;
            }
            return result;
        }
        return new Dictionary<string, double>();
    }
}
