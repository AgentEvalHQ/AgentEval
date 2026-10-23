// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Evals;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.StoreV1;

// Legacy presentation only. Native results stay canonical; no EvalResult is synthesized for them.
internal static class StoreV1CapturedTreeReader
{
    internal static EvalResult? Read(AefRunEvidence run, string resultId)
    {
        var root = run.Results.SingleOrDefault(r => r.GetProperty("resultId").GetString() == resultId);
        if (root.ValueKind == JsonValueKind.Undefined || !run.Info.Imported || root.TryGetProperty("parentResultId", out _)
            || !root.TryGetProperty("ext", out var ext) || !ext.TryGetProperty(StoreV1Exporter.ExtName, out var legacy)) return null;
        var ids = root.TryGetProperty("evidence", out var evidence) ? evidence.EnumerateArray()
            .Select(e => e.GetString()!).Where(id => id.StartsWith("E-s", StringComparison.Ordinal) && id.EndsWith("-eval", StringComparison.Ordinal)).ToArray() : [];
        if (ids.Length == 0) return null;
        if (ids.Length != 1) throw Bad("Ambiguous captured result trees.");
        if (run.CapturedContent(ids[0]) is not { } bytes) return null;
        EvalResult tree;
        try { tree = StoreV1EvalTree.Parse(new UTF8Encoding(false, true).GetString(bytes.Span)); }
        catch (DecoderFallbackException error) { throw Bad("Captured tree is not UTF-8.", error); }
        CheckPresentationFacts(tree);
        var actual = JsonNode.Parse(root.GetRawText())!;
        CheckRoot(actual, tree);
        // Older imports legitimately kept descendants only in the captured artifact.
        if (!legacy.TryGetProperty("evalTreeProjectionVersion", out var version)) return tree;
        if (version.ValueKind != JsonValueKind.Number || version.GetDouble() != 1) throw Bad("Unsupported captured-tree projection version.");
        var caseId = root.GetProperty("caseId").GetString()!;
        var mapped = run.Results.Where(r => r.GetProperty("caseId").GetString() == caseId
            && r.GetProperty("path").GetString()!.StartsWith("scenario/eval/", StringComparison.Ordinal))
            .ToDictionary(r => r.GetProperty("path").GetString()!, r => JsonNode.Parse(r.GetRawText())!, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (child, i) in (tree.Details.SubResults ?? []).Select((node, i) => (node, i)))
            CheckChild(child, "scenario/eval/" + (i + 1).ToString(CultureInfo.InvariantCulture), resultId);
        if (visited.Count != mapped.Count) throw Bad("Captured descendants disagree with the canonical tree.");
        return tree;

        void CheckChild(EvalResult node, string path, string parentId)
        {
            if (!mapped.TryGetValue(path, out var line) || !visited.Add(path)) throw Bad("A captured descendant is missing from the canonical tree.");
            // The converter's first descendant score uses its collision-safe normalized metric ID.
            // Absent nodes have no scores; the metric is then irrelevant to their projection.
            var metric = (string?)line["scores"]?[0]?["metric"] ?? "agenteval.eval.score";
            if (line["scores"] is JsonArray { Count: > 0 })
            {
                var definition = run.Metrics.GetProperty("metrics").EnumerateArray().Single(m => m.GetProperty("id").GetString() == metric);
                if (definition.GetProperty("kind").GetString() != AefNames.Of(AefMetricKind.Score)
                    || definition.GetProperty("direction").GetString() != AefNames.Of(AefMetricDirection.HigherBetter)
                    || definition.GetProperty("scale") is not { ValueKind: JsonValueKind.Object } scale
                    || scale.GetProperty("min").GetDouble() != 0 || scale.GetProperty("max").GetDouble() != 1
                    || definition.TryGetProperty("unit", out _))
                    throw Bad("Captured descendant score conflicts with its canonical metric definition.");
            }
            var projected = StoreV1Exporter.ProjectTree(node, caseId, path, metric,
                (string)root.GetProperty("lane").GetString()!, AefContentCapture.On).Result;
            var expected = projected.ToJson((string)line["resultId"]!, parentId);
            foreach (var field in new[] { "parentResultId", "caseId", "path", "evaluator", "state", "scores", "severity", "verdictRule", "annotator", "usage", "ext" })
                if (!Matches(expected[field], line[field])) throw Bad("Captured descendant conflicts with canonical " + field + ".");
            foreach (var (child, i) in (node.Details.SubResults ?? []).Select((child, i) => (child, i)))
                CheckChild(child, path + "/" + (i + 1).ToString(CultureInfo.InvariantCulture), (string)line["resultId"]!);
        }
    }

    private static void CheckPresentationFacts(EvalResult node)
    {
        if (string.IsNullOrWhiteSpace(node.Metric.Name) || string.IsNullOrWhiteSpace(node.Metric.Category)
            || string.IsNullOrWhiteSpace(node.Provenance.Type)) throw Bad("Required display metadata is missing.");
        foreach (var child in node.Details.SubResults ?? []) CheckPresentationFacts(child);
    }

    private static void CheckRoot(JsonNode line, EvalResult tree)
    {
        var state = EvalScoreState.Of(tree.Score);
        if ((string?)line["evaluator"]?["id"] != tree.Metric.Key
            || line["evaluator"]?["version"] is { } version && (string?)version != tree.Metric.Version
            || (string?)line["state"] != AefNames.Of(state)) throw Bad("Captured root identity or state conflicts with canonical evidence.");
        var expectedScores = new Dictionary<string, double>(StringComparer.Ordinal) { ["score"] = tree.Score.Value };
        foreach (var (name, value) in tree.Details.Dimensions ?? new Dictionary<string, double>())
            if (StoreV1Exporter.DimensionMetric(name) is { } metric) expectedScores[metric] = value;
        foreach (var score in line["scores"]?.AsArray() ?? [])
            if (expectedScores.TryGetValue((string)score!["metric"]!, out var expected) && (double?)score["value"] != expected)
                throw Bad("Captured root score conflicts with canonical evidence.");
        if (line["verdictRule"]?["threshold"] is { } threshold && (double?)threshold != tree.Score.Threshold)
            throw Bad("Captured threshold conflicts with canonical evidence.");
        if (line["severity"] is { } severity && (string?)severity != tree.Score.Severity)
            throw Bad("Captured severity conflicts with canonical evidence.");
        if (line["annotator"] is { } annotator)
        {
            var expected = StoreV1Exporter.Annotator(EvalResultPersistence.ComparabilityOf(tree, null), tree)?.ToJson();
            if (!Matches(expected, annotator)) throw Bad("Captured grading instrument conflicts with canonical evidence.");
        }
        foreach (var usage in line["usage"]?.AsArray() ?? [])
            if ((string?)usage?["role"] == "other" && usage?["costUsd"] is { } cost && (double?)cost != tree.Provenance.EstimatedCost)
                throw Bad("Captured cost conflicts with canonical evidence.");
    }

    // Compare mapped facts, permitting unfamiliar object members. Numbers compare by value, not JSON spelling.
    private static bool Matches(JsonNode? expected, JsonNode? actual)
    {
        if (expected is null || actual is null) return expected is null && actual is null;
        if (expected is JsonObject obj) return actual is JsonObject other && obj.All(p => Matches(p.Value, other[p.Key]));
        if (expected is JsonArray array) return actual is JsonArray other && array.Count == other.Count && array.Where((v, i) => !Matches(v, other[i])).Any() == false;
        if (expected.GetValueKind() == JsonValueKind.Number && actual.GetValueKind() == JsonValueKind.Number)
            return JsonSerializer.SerializeToElement(expected).GetDouble() == JsonSerializer.SerializeToElement(actual).GetDouble();
        return JsonNode.DeepEquals(expected, actual);
    }
    private static InvalidDataException Bad(string actual, Exception? inner = null) => new(
        "Expected: captured legacy details consistent with their canonical result. Actual: " + actual +
        " Suggestions: inspect the original artifact. Because: rich presentation must not replace recorded result facts.", inner);
}
