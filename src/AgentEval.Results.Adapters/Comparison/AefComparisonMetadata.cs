// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentEval.Evals;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Comparison;

// Producer-owned comparison observations. Facts are derived while the original evaluation is available;
// readers must not guess them from an omitted annotator or reconstruct them from a rounded display value.
internal static class AefComparisonMetadata
{
    internal const string Extension = "agenteval.comparison";
    internal const string OmittedDerivation = "Derivation text omitted by content-capture policy.";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal static JsonObject Attach(JsonObject? extensions, EvalResult result, AefContentCapture capture,
        bool? selected = null, string? id = null, string? name = null)
    {
        var output = extensions?.DeepClone().AsObject() ?? new JsonObject();
        if (output.ContainsKey(Extension))
            throw new ArgumentException("Expected: producer-owned comparison facts. Actual: reserved comparison extension supplied. " +
                "Suggestions: use another extension name. Because: caller metadata cannot replace observed comparison facts.");
        var facts = EvalResultPersistence.ComparabilityOf(result, result.Observation?.SubjectModel);
        if (capture == AefContentCapture.Off && facts.ChanceFloor is { } floor)
            facts = facts with { ChanceFloor = floor with { Derivation = OmittedDerivation } };
        output[Extension] = new JsonObject
        {
            ["version"] = 1,
            ["selected"] = selected ?? result.Details.SubResults is not { Count: > 0 },
            ["id"] = id ?? result.Metric.Key,
            ["name"] = name,
            ["metric"] = "score",
            ["stimulusHash"] = capture == AefContentCapture.On ? AgentEval.Output.StimulusHash.Of(result.Observation?.Query) : null,
            ["facts"] = JsonSerializer.SerializeToNode(facts, Json),
        };
        return output;
    }
}
