// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Evals.Agentic.Calibration;

namespace AgentEval.Evals.Agentic.JudgeQuality;

/// <summary>
/// Meta-evaluator that takes a list of (judge result, expected verdict) pairs and computes
/// the fraction where the judge's label matches the expected label (calibration accuracy).
/// Pure-code — no LLM invocation.
/// <para>
/// <b>Input contract</b> (<see cref="EvalInput.Metadata"/> keys):
/// <list type="bullet">
///   <item>
///     <c>"calibration_pairs"</c> (required) — one of:
///     <list type="bullet">
///       <item>
///         An <see cref="IEnumerable{T}"/> of <c>(string Expected, string Actual)</c> tuples.
///       </item>
///       <item>
///         A JSON array string of objects with <c>expected</c> and <c>actual</c> string fields.
///       </item>
///     </list>
///   </item>
/// </list>
/// </para>
/// <para>
/// <b>Score semantics</b>: fraction in [0, 1] where 1.0 = every judge verdict matches the
/// expected verdict. Passes when accuracy &gt;= <c>passThreshold</c>.
/// Severity: <c>low</c> (meta-metric).
/// </para>
/// </summary>
public sealed class CalibrationAccuracyEval : IEval
{
    private readonly double _passThreshold;

    /// <inheritdoc/>
    public string Key => "calibration_accuracy";

    /// <inheritdoc/>
    public string Name => "Calibration Accuracy";

    /// <inheritdoc/>
    public string Category => "judge-quality";

    /// <inheritdoc/>
    public string Version => "1.0.0";

    /// <summary>
    /// Initialises a new <see cref="CalibrationAccuracyEval"/>.
    /// </summary>
    /// <param name="passThreshold">
    /// Accuracy fraction at or above which the eval passes. Defaults to 0.85.
    /// </param>
    public CalibrationAccuracyEval(double passThreshold = 0.85)
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

        var pairs = ExtractPairs(input);

        if (pairs.Count == 0)
        {
            return Task.FromResult(EvalResult.Skipped(this,
                "CalibrationAccuracyEval requires calibration pairs in EvalInput.Metadata[\"calibration_pairs\"]."));
        }

        var accuracy = CalibrationMetrics.Accuracy(pairs);

        var passed = accuracy >= _passThreshold;
        var label = passed ? "pass" : "fail";
        var severity = passed ? "none" : "low";

        return Task.FromResult(new EvalResult(
            Metric: new(Key, Name, Category, Version),
            Score: new(accuracy, null, label, passed, _passThreshold, severity, null),
            Details: new(
                Dimensions: new Dictionary<string, double>
                {
                    ["accuracy"] = accuracy,
                    ["pair_count"] = pairs.Count,
                    ["correct_count"] = Math.Round(accuracy * pairs.Count),
                },
                Evidence: null,
                Recommendations: passed
                    ? null
                    : new[] { $"Calibration accuracy ({accuracy:P0}) is below threshold ({_passThreshold:P0}). Re-run calibration with an updated judge." },
                SubResults: null,
                AggregationStrategy: "accuracy"),
            Provenance: new("code", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IReadOnlyList<(string Expected, string Actual)> ExtractPairs(EvalInput input)
    {
        if (input.Metadata is null || !input.Metadata.TryGetValue("calibration_pairs", out var raw))
            return Array.Empty<(string, string)>();

        return raw switch
        {
            IEnumerable<(string Expected, string Actual)> typed => typed.ToList(),
            string json => ParseJsonPairs(json),
            _ => TryConvertDynamic(raw)
        };
    }

    private static IReadOnlyList<(string Expected, string Actual)> ParseJsonPairs(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<(string, string)>();

            return doc.RootElement
                .EnumerateArray()
                .Where(el => el.TryGetProperty("expected", out _) && el.TryGetProperty("actual", out _))
                .Select(el => (
                    Expected: el.GetProperty("expected").GetString() ?? "unknown",
                    Actual: el.GetProperty("actual").GetString() ?? "unknown"))
                .ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<(string, string)>();
        }
    }

    private static IReadOnlyList<(string Expected, string Actual)> TryConvertDynamic(object raw)
    {
        // Handle IEnumerable<object> where each element might be a tuple-like structure
        if (raw is global::System.Collections.IEnumerable enumerable)
        {
            var result = new List<(string, string)>();
            foreach (var item in enumerable)
            {
                if (item is ValueTuple<string, string> t)
                    result.Add((t.Item1, t.Item2));
            }
            return result;
        }
        return Array.Empty<(string, string)>();
    }
}
