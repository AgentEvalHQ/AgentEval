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
    public static ScenarioResult ToScenarioResult(EvalResult result, string scenarioId, string scenarioName)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioName);

        var json = JsonSerializer.Serialize(result, s_jsonOpts);
        var dimensions = result.Details.Dimensions ?? new Dictionary<string, double>();

        return new ScenarioResult(
            Id: scenarioId,
            Name: scenarioName,
            Input: "",
            Output: json,
            Passed: result.Score.Passed,
            Score: result.Score.Value,
            Metrics: dimensions,
            Assertions: Array.Empty<AssertionResult>(),
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
