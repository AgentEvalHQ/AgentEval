// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

using System.Text.Json;
using AgentEval.Output;

/// <summary>
/// Maps recursive EvalResult trees into the flat ScenarioResult shape that
/// IOutputStore.WriteScenarioResultAsync persists. Score, pass-state, dimensions,
/// and cost are lifted to top-level ScenarioResult fields for queryability;
/// the full recursive tree is preserved as JSON inside ScenarioResult.Output.
/// </summary>
public static class EvalResultPersistence
{
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>Builds a ScenarioResult that stores <paramref name="result"/> as JSON in Output.</summary>
    /// <param name="result">The eval-result tree to persist.</param>
    /// <param name="scenarioId">Scenario identifier.</param>
    /// <param name="scenarioName">Human-readable scenario name.</param>
    /// <param name="assertions">
    /// Assertion outcomes to persist alongside the scenario. When <see langword="null"/> the
    /// outcomes collected by the ambient <see cref="AgentEvalScope"/> are used, so an eval that
    /// runs its assertions inside <c>AgentEvalScope.Collecting()</c> gets them into the artifact
    /// with no extra wiring. Pass an empty list to persist none. (AE-01: before this, the field was
    /// hard-coded empty and assertion outcomes reached no artifact AgentEval writes.)
    /// </param>
    public static ScenarioResult ToScenarioResult(
        EvalResult result,
        string scenarioId,
        string scenarioName,
        IReadOnlyList<AssertionResult>? assertions = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioName);

        var json = JsonSerializer.Serialize(result, s_jsonOpts);

        // Build a Metrics dictionary that lifts queryable-from-the-flat-shape
        // fields out of the recursive tree: dimensions (already there);
        // confidence + severity-as-ordinal so callers querying the store
        // by `ScenarioResult.Metrics` don't have to deserialise the JSON
        // tree. Severity is encoded as a small ordinal {none=0, low=1,
        // medium=2, high=3, critical=4} for numeric comparison.
        //
        // Lifted keys are prefixed with `_lifted.` to avoid silently
        // overwriting consumer-supplied Dimensions that legitimately use
        // domain words like "confidence" or "severity_ordinal" as criterion
        // names. Reads must use the `_lifted.*` form.
        const string ConfidenceKey      = "_lifted.confidence";
        const string SeverityOrdinalKey = "_lifted.severity_ordinal";

        var metrics = result.Details.Dimensions is { } dims
            ? new Dictionary<string, double>(dims)
            : new Dictionary<string, double>();
        if (result.Score.Confidence is { } conf)
            metrics[ConfidenceKey] = conf;
        metrics[SeverityOrdinalKey] = result.Score.Severity switch
        {
            "critical" => 4,
            "high"     => 3,
            "medium"   => 2,
            "low"      => 1,
            _          => 0,
        };

        return new ScenarioResult(
            Id: scenarioId,
            Name: scenarioName,
            Input: "",
            Output: json,
            Passed: result.Score.Passed,
            Score: result.Score.Value,
            Metrics: metrics,
            Assertions: assertions
                ?? AgentEval.Assertions.AgentEvalScope.Current?.Results
                ?? Array.Empty<AssertionResult>(),
            Duration: TimeSpan.Zero,
            EstimatedCost: result.Provenance.EstimatedCost);
    }

    /// <summary>Restores an EvalResult from a ScenarioResult previously produced by <see cref="ToScenarioResult"/>.</summary>
    public static EvalResult? FromScenarioResult(ScenarioResult sr)
    {
        ArgumentNullException.ThrowIfNull(sr);
        if (string.IsNullOrEmpty(sr.Output)) return null;
        try
        {
            return JsonSerializer.Deserialize<EvalResult>(sr.Output, s_jsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
