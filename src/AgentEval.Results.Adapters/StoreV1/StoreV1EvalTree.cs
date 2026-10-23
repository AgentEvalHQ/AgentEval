// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.Evals;

namespace AgentEval.Results.Adapters.StoreV1;

// Shared syntax/required-fact boundary for conversion and captured legacy presentation.
internal static class StoreV1EvalTree
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 128,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private static InvalidDataException InvalidSource(string actual) => new(
        "Expected: a complete recorded eval-result tree. Actual: " + actual +
        " Suggestions: inspect the original evidence. Because: missing facts must not acquire constructor defaults.");
    // These limits refuse oversized trees rather than silently publishing a partial topology.
    private const int MaxEvalTreeNodes = 10_000;
    private const int MaxEvalTreeDepth = 32;

    internal static string Serialize(EvalResult tree) => JsonSerializer.Serialize(tree, Json);

    internal static EvalResult? TryParse(string output) => LooksLikeEvalTree(output) ? Parse(output) : null;

    internal static EvalResult Parse(string output)
    {
        // Identify the envelope before typed deserialization. An invalid recognized tree must not
        // fall through to an ordinary flat scenario when the legacy deserializer returns null.
        try
        {
            using var json = JsonDocument.Parse(output, new() { MaxDepth = 128 });
            CheckUniqueMembers(json.RootElement);
            var rawCount = 0;
            ValidateRaw(json.RootElement, 0);
            var tree = json.RootElement.Deserialize<EvalResult>(new JsonSerializerOptions(Json) { MaxDepth = 128 })
                ?? throw InvalidSource("The eval-result tree is null.");
            var count = 0;
            Validate(json.RootElement, tree, 0);
            return tree;

            void ValidateRaw(JsonElement node, int depth)
            {
                if (++rawCount > MaxEvalTreeNodes || depth > MaxEvalTreeDepth)
                    throw InvalidSource("The eval-result tree exceeds the supported node or depth limit.");
                CheckObject(node);
                foreach (var section in new[] { "metric", "score", "provenance", "details" })
                {
                    if (!node.TryGetProperty(section, out var part))
                        throw InvalidSource("The eval-result tree omits a required section.");
                    CheckObject(part);
                }
                if (!node.GetProperty("details").TryGetProperty("subResults", out var children) ||
                    children.ValueKind == JsonValueKind.Null) return;
                if (children.ValueKind != JsonValueKind.Array)
                    throw InvalidSource("The eval-result tree's children must be an array.");
                foreach (var child in children.EnumerateArray()) ValidateRaw(child, depth + 1);
            }

            void Validate(JsonElement raw, EvalResult node, int depth)
            {
                if (++count > MaxEvalTreeNodes || depth > MaxEvalTreeDepth)
                    throw InvalidSource("The eval-result tree exceeds the supported node or depth limit.");
                if (node?.Metric is null || node.Score is null || node.Details is null || node.Provenance is null ||
                    !AefConverter.IsResultText(node.Metric.Key, 256) || !AefConverter.IsResultText(node.Metric.Version, 64) ||
                    node.EvaluatedAt == default || !Enum.IsDefined(node.Score.Measurement) ||
                    node.Score.Value is < 0 or > 1 || string.IsNullOrWhiteSpace(node.Score.Label))
                    throw InvalidSource("The eval-result tree has incomplete or invalid evaluator facts.");
                // Constructor defaults are not observations. Require the value-type facts before projecting them.
                if (!raw.TryGetProperty("score", out var score) || score.ValueKind != JsonValueKind.Object ||
                    !score.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Number ||
                    !score.TryGetProperty("passed", out var passed) || passed.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    !raw.TryGetProperty("provenance", out var provenance) || provenance.ValueKind != JsonValueKind.Object ||
                    !provenance.TryGetProperty("cacheHit", out var cache) || cache.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw InvalidSource("The eval-result tree omits a required score or provenance fact.");
                if (node.Details.SubResults is not { Count: > 0 } children) return;
                var rawChildren = raw.GetProperty("details").GetProperty("subResults");
                for (var i = 0; i < children.Count; i++) Validate(rawChildren[i], children[i], depth + 1);
            }
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new InvalidDataException("Expected a complete, bounded eval-result tree. Actual: invalid recorded tree. "
                + "Suggestions: repair the source evidence. Because: conversion must not flatten or invent evaluator facts.", error);
        }
    }

    private static void CheckObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count())
            throw InvalidSource("The eval-result tree requires objects with unique member names.");
    }

    private static void CheckUniqueMembers(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw InvalidSource("The eval-result tree has duplicate JSON members.");
                CheckUniqueMembers(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) CheckUniqueMembers(child);
    }

    private static bool LooksLikeEvalTree(string output)
    {
        // Root metric/score names alone are ordinary agent output too. Recognize a metric's
        // key/version shape or the characteristic EvalResult envelope before claiming a tree.
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(output), new JsonReaderOptions { MaxDepth = int.MaxValue });
        var metric = false;
        var score = false;
        var metricIdentity = false;
        var envelope = false;
        var details = false;
        var provenance = false;
        var scoreObject = false;
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) return false;
                var isMetric = reader.ValueTextEquals("metric");
                metric |= isMetric;
                score |= reader.ValueTextEquals("score");
                details |= reader.ValueTextEquals("details");
                provenance |= reader.ValueTextEquals("provenance");
                envelope |= details || provenance || reader.ValueTextEquals("evaluatedAt");
                if (metricIdentity && (score || envelope) || metric && score && envelope || scoreObject && details && provenance) return true;
                var isScore = reader.ValueTextEquals("score");
                if (!reader.Read()) break;
                scoreObject |= isScore && reader.TokenType == JsonTokenType.StartObject;
                if (scoreObject && details && provenance) return true;
                if (isMetric && reader.TokenType == JsonTokenType.StartObject)
                {
                    var probe = reader;
                    while (probe.Read() && probe.TokenType != JsonTokenType.EndObject)
                    {
                        if (probe.TokenType != JsonTokenType.PropertyName) break;
                        metricIdentity |= probe.ValueTextEquals("key") || probe.ValueTextEquals("version");
                        if (!probe.Read()) break;
                        probe.Skip();
                    }
                    if (metricIdentity && (score || envelope)) return true;
                }
                reader.Skip();
            }
        }
        catch (JsonException) { return metricIdentity; }
        return false;
    }

}
