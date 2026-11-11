// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.Core;

namespace AgentEval.Prompts;

/// <summary>Implementation-owned validation for configured RAG response contracts.</summary>
internal static class JudgeResponseReader
{
    internal static bool ValidateEvaluation(string? reply, JudgePromptDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(reply) || reply.Length > 1048576) return false;
        try
        {
            using var document = JsonDocument.Parse(reply, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || HasDuplicateProperties(root)) return false;
            var criteriaContract = definition.ResponseContract == JudgeResponseContract.CriteriaV1;
            var scoreKey = criteriaContract ? "overallScore" : "score";
            if (!root.TryGetProperty(scoreKey, out var score) || score.ValueKind != JsonValueKind.Number ||
                !score.TryGetDouble(out var number) || !ChatClientEvaluator.TryReadOnScale(number, definition.Scale, out _, out _)) return false;
            var narrative = criteriaContract ? "summary" : "reasoning";
            if (!IsString(root, narrative)) return false;
            var hasImprovements = root.TryGetProperty("improvements", out var improvements);
            if (criteriaContract && !hasImprovements || hasImprovements &&
                (improvements.ValueKind != JsonValueKind.Array || improvements.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))) return false;
            var hasCriteria = root.TryGetProperty("criteriaResults", out var criteria);
            if (root.TryGetProperty("criteria_results", out var snakeCriteria))
            {
                if (hasCriteria || criteriaContract) return false;
                hasCriteria = true;
                criteria = snakeCriteria;
            }
            if (hasCriteria)
            {
                if (criteria.ValueKind != JsonValueKind.Array || criteria.GetArrayLength() != definition.Criteria.Count) return false;
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in criteria.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || !IsString(item, "id") || !IsString(item, "explanation")) return false;
                    var id = item.GetProperty("id").GetString()!;
                    if (!ids.Add(id) || !definition.Criteria.Any(c => c.Id == id)) return false;
                    var applicable = true;
                    if (item.TryGetProperty("applicable", out var applies))
                    {
                        if (applies.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
                        applicable = applies.GetBoolean();
                    }
                    if (item.TryGetProperty("met", out var met))
                    {
                        if (met.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
                    }
                    else if (applicable) return false;
                    if (item.EnumerateObject().Any(p => p.Name is not ("id" or "criterion" or "met" or "applicable" or "explanation"))) return false;
                    if (item.TryGetProperty("criterion", out var text) && text.ValueKind != JsonValueKind.String) return false;
                }
            }
            else if (criteriaContract) return false;
            if (root.TryGetProperty("label", out var label) && label.ValueKind != JsonValueKind.String) return false;
            if (root.TryGetProperty("severity", out var severity) && severity.ValueKind != JsonValueKind.String) return false;
            if (root.TryGetProperty("evidence", out var evidence))
            {
                if (evidence.ValueKind != JsonValueKind.Array || evidence.GetArrayLength() > 4096) return false;
                foreach (var item in evidence.EnumerateArray())
                    if (item.ValueKind != JsonValueKind.Object || !IsString(item, "source") || !IsString(item, "reference") || !IsString(item, "message") ||
                        item.EnumerateObject().Any(p => p.Name is not ("source" or "reference" or "message"))) return false;
            }
            return root.EnumerateObject().All(p => p.Name == scoreKey || p.Name == narrative ||
                p.Name is "criteriaResults" or "criteria_results" or "improvements" or "label" or "severity" or "evidence");
        }
        catch (JsonException) { return false; }
    }

    private static bool IsString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String;

    internal static bool TryReadRag(string? reply, JudgePromptDefinition definition,
        out JsonElement root, out double score, out JudgeReplyFailure failure)
    {
        root = default;
        score = 0;
        failure = JudgeReplyFailure.InvalidJson;
        if (string.IsNullOrWhiteSpace(reply)) { failure = JudgeReplyFailure.Empty; return false; }
        if (reply.Length > 1048576) return false;
        try
        {
            using var document = JsonDocument.Parse(reply, new JsonDocumentOptions { MaxDepth = 16 });
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object) { failure = JudgeReplyFailure.NotAnObject; return false; }
            if (HasDuplicateProperties(value)) { failure = JudgeReplyFailure.Ambiguous; return false; }
            if (!value.TryGetProperty("score", out var number)) { failure = JudgeReplyFailure.NoScore; return false; }
            if (number.ValueKind != JsonValueKind.Number || !number.TryGetDouble(out var raw))
            { failure = JudgeReplyFailure.ScoreNotANumber; return false; }
            if (!ChatClientEvaluator.TryReadOnScale(raw, definition.Scale, out var unit, out _))
            { failure = JudgeReplyFailure.ScoreOutOfRange; return false; }
            string[] arrays = definition.ResponseContract switch
            {
                JudgeResponseContract.FaithfulnessV1 => ["faithfulClaims", "hallucinatedClaims"],
                JudgeResponseContract.RelevanceV1 => ["irrelevantParts"],
                JudgeResponseContract.ContextPrecisionV1 => ["relevantParts", "irrelevantParts"],
                JudgeResponseContract.ContextRecallV1 => ["informationPresent", "informationMissing"],
                JudgeResponseContract.AnswerCorrectnessV1 => ["factsCorrect", "factsIncorrect", "factsMissing"],
                _ => throw new ArgumentException("Expected: a RAG response contract. Actual: unsupported contract. Suggestions: use the corresponding evaluator. Because: reply schemas are implementation-owned.")
            };
            if (!value.TryGetProperty("reasoning", out var reasoning) || reasoning.ValueKind != JsonValueKind.String) return false;
            foreach (var name in arrays)
                if (!value.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array ||
                    array.GetArrayLength() > 4096 || array.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String)) return false;
            if (definition.ResponseContract == JudgeResponseContract.RelevanceV1)
                foreach (var name in new[] { "addressesQuestion", "staysOnTopic" })
                    if (!value.TryGetProperty(name, out var boolean) || boolean.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
            var allowed = arrays.Concat(new[] { "score", "reasoning" }).ToHashSet(StringComparer.Ordinal);
            if (definition.ResponseContract == JudgeResponseContract.RelevanceV1) allowed.UnionWith(["addressesQuestion", "staysOnTopic"]);
            if (value.EnumerateObject().Any(p => !allowed.Contains(p.Name))) return false;
            root = value.Clone();
            score = unit * 100;
            failure = JudgeReplyFailure.None;
            return true;
        }
        catch (JsonException) { return false; }
    }

    internal static bool HasDuplicateProperties(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() !=
            element.EnumerateObject().Count() || element.EnumerateObject().Any(p => HasDuplicateProperties(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Any(HasDuplicateProperties),
        _ => false
    };
}
