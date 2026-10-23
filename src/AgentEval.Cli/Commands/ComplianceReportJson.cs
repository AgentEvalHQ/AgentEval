// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace AgentEval.Cli.Commands;

// The CLI knows the reporting assemblies. Results.Adapters retains rich payloads without depending on those packs.
internal static class ComplianceReportJson
{
    private static readonly ConcurrentDictionary<Type, JsonSchema> Schemas = new();

    internal static T Read<T>(JsonElement document, JsonSerializerOptions options, string schemaName)
    {
        var schema = Schemas.GetOrAdd(typeof(T), type =>
        {
            var name = type.Assembly.GetManifestResourceNames().Single(n => n.EndsWith("." + schemaName, StringComparison.Ordinal));
            using var stream = type.Assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            return JsonSchema.FromText(reader.ReadToEnd());
        });
        if (!schema.Evaluate(JsonNode.Parse(document.GetRawText()), new() { OutputFormat = OutputFormat.Flag }).IsValid)
            throw Bad("The rich report violates its recorded format schema.");
        ValidateTree(document.GetProperty("compositeTree"));
        foreach (var finding in document.GetProperty("criticalFindings").EnumerateArray()) ValidateTree(finding);
        return document.Deserialize<T>(options) ?? throw Bad("The captured report is null.");
    }

    private static void ValidateTree(JsonElement node)
    {
        var metric = Member(node, "metric", JsonValueKind.Object);
        foreach (var name in new[] { "key", "name", "category", "version" }) Member(metric, name, JsonValueKind.String);
        var score = Member(node, "score", JsonValueKind.Object);
        Range(Member(score, "value", JsonValueKind.Number), "score.value");
        Member(score, "label", JsonValueKind.String); Member(score, "severity", JsonValueKind.String);
        if (!score.TryGetProperty("passed", out var passed) || passed.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Bad("score.passed is missing or invalid.");
        foreach (var name in new[] { "threshold", "confidence" })
            if (score.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null) Range(value, "score." + name);
        var provenance = Member(node, "provenance", JsonValueKind.Object);
        Member(provenance, "type", JsonValueKind.String);
        if (!provenance.TryGetProperty("cacheHit", out var cacheHit) || cacheHit.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Bad("provenance.cacheHit is missing or invalid.");
        if (!Member(node, "evaluatedAt", JsonValueKind.String).TryGetDateTimeOffset(out _)) throw Bad("evaluatedAt is not a recorded timestamp.");
        var details = Member(node, "details", JsonValueKind.Object);
        if (details.TryGetProperty("subResults", out var children) && children.ValueKind != JsonValueKind.Null)
        {
            if (children.ValueKind != JsonValueKind.Array) throw Bad("details.subResults is not an array.");
            foreach (var child in children.EnumerateArray()) ValidateTree(child);
        }
    }

    private static void Range(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number) || number is < 0 or > 1)
            throw Bad(name + " is not a recorded normalized score.");
    }
    private static JsonElement Member(JsonElement value, string name, JsonValueKind kind) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var member) && member.ValueKind == kind
            ? member : throw Bad(name + " is missing or has the wrong type.");
    private static InvalidDataException Bad(string actual) => new("Expected: a complete recorded compliance report. Actual: " + actual +
        " Suggestions: select the original valid report. Because: absent fields must not be rendered as measured zeroes or default outcomes.");
}
