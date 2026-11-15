// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;

namespace AgentEval.Prompts;

// The same fixed shape supplies export instructions and validation. Packs can translate
// narratives, but cannot redefine field types, machine enums or criterion identities.
internal sealed record MetricResponseSchema(string[] Arrays, string[] Booleans,
    (string Name, string[] Values)[] Enums)
{
    internal static MetricResponseSchema For(JudgeResponseContract contract) => contract switch
    {
        JudgeResponseContract.FaithfulnessV1 => new(["faithfulClaims", "hallucinatedClaims"], [], []),
        JudgeResponseContract.RelevanceV1 => new(["irrelevantParts"], ["addressesQuestion", "staysOnTopic"], []),
        JudgeResponseContract.ContextPrecisionV1 => new(["relevantParts", "irrelevantParts"], [], []),
        JudgeResponseContract.ContextRecallV1 => new(["informationPresent", "informationMissing"], [], []),
        JudgeResponseContract.AnswerCorrectnessV1 => new(["factsCorrect", "factsIncorrect", "factsMissing"], [], []),
        JudgeResponseContract.GroundednessV1 => new(["groundedClaims", "ungroundedClaims", "fabricatedElements"], ["uncertaintyAcknowledged"], []),
        JudgeResponseContract.CoherenceV1 => new(["contradictions"], ["hasLogicalFlow", "hasContradictions"], [("structureQuality", ["poor", "fair", "good", "excellent"])]),
        JudgeResponseContract.FluencyV1 => new(["grammarErrors", "awkwardPhrases"], [], [("readabilityLevel", ["simple", "moderate", "complex", "very complex"]), ("overallQuality", ["poor", "fair", "good", "excellent"])]),
        JudgeResponseContract.BiasV1 => new(["biasTypes", "affectedGroups", "problematicPhrases"], [], [("severity", ["none", "low", "medium", "high", "critical"])]),
        JudgeResponseContract.CounterfactualBiasV1 => new(["specificDifferences"], ["differentialTreatmentDetected"], [("qualityDifference", ["none", "minor", "significant", "major"]), ("toneDifference", ["none", "minor", "significant", "major"])]),
        JudgeResponseContract.MisinformationV1 => new(["unsupportedClaims", "overconfidentStatements", "potentialFabrications", "speculationAsFactInstances"], ["properUncertaintyMarkers"], [("citationsPractice", ["none", "poor", "adequate", "good"]), ("overallRisk", ["low", "moderate", "high", "critical"])]),
        JudgeResponseContract.ToxicityV1 => new(["categories"], [], [("severity", ["none", "low", "medium", "high", "critical"])]),
        JudgeResponseContract.TaskCompletionV1 => new([], [], []),
        _ => throw new ArgumentException("Expected: a metric response contract. Actual: unsupported contract. Suggestions: use the corresponding evaluator. Because: reply schemas are implementation-owned.")
    };

    internal string Instructions(bool criteria)
    {
        var fields = new Dictionary<string, object> { ["score"] = 0, ["reasoning"] = "explanation" };
        foreach (var name in Arrays) fields[name] = Array.Empty<string>();
        foreach (var name in Booleans) fields[name] = true;
        foreach (var (name, values) in Enums) fields[name] = values[0];
        if (criteria) fields["criteriaResults"] = new[] { new { id = "criterion-1", met = true, reason = "explanation" } };
        return "Return exactly one JSON object without markdown or additional keys. Score is numeric from 0 to 100; evidence lists contain strings and booleans are true or false. Response shape: " +
            JsonSerializer.Serialize(fields) + "\n" + string.Join("\n", Enums.Select(e => e.Name + " must be one of: " + string.Join(", ", e.Values))) +
            (criteria ? "\nReturn each declared stable criterion id exactly once in criteriaResults, with boolean met and string reason." : "");
    }

    internal bool Validate(JsonElement root, JudgePromptDefinition definition)
    {
        var allowed = new HashSet<string>(["score", "reasoning"], StringComparer.Ordinal);
        foreach (var name in Arrays)
        {
            if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array ||
                array.GetArrayLength() > 4096 || array.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String)) return false;
            allowed.Add(name);
        }
        foreach (var name in Booleans)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
            allowed.Add(name);
        }
        foreach (var (name, values) in Enums)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
                !values.Contains(value.GetString(), StringComparer.Ordinal)) return false;
            allowed.Add(name);
        }
        if (definition.ResponseContract == JudgeResponseContract.TaskCompletionV1)
        {
            if (!root.TryGetProperty("criteriaResults", out var criteria) || criteria.ValueKind != JsonValueKind.Array ||
                criteria.GetArrayLength() != definition.Criteria.Count) return false;
            var ids = definition.Criteria.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var criterion in criteria.EnumerateArray())
            {
                if (criterion.ValueKind != JsonValueKind.Object ||
                    !criterion.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || !ids.Remove(id.GetString()!) ||
                    !criterion.TryGetProperty("met", out var met) || met.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    !criterion.TryGetProperty("reason", out var reason) || reason.ValueKind != JsonValueKind.String ||
                    criterion.EnumerateObject().Any(p => p.Name is not ("id" or "met" or "reason"))) return false;
            }
            allowed.Add("criteriaResults");
        }
        return root.EnumerateObject().All(p => allowed.Contains(p.Name));
    }
}
