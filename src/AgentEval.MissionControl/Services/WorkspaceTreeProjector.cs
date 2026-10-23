// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.MissionControl.GraphQL;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Adapters.StoreV1;

namespace AgentEval.MissionControl.Services;

internal static class WorkspaceTreeProjector
{
    private static readonly string[] AbsenceStates = ["not_measured", "not_applicable", "skipped", "error", "pending"];
    internal static WorkspaceResultTree? Tree(AefWorkspaceRun run, string caseId, string? resultId)
    {
        var lines = run.Evidence.Results.Where(r => Text(r, "caseId") == caseId).ToArray();
        if (lines.Length == 0 || resultId is not null && !lines.Any(r => Text(r, "resultId") == resultId)) return null;
        // A case view is all-or-nothing. Large evidence needs a paged viewer, never an undisclosed partial tree.
        if (lines.Length > 10_000)
            throw new InvalidDataException("Expected: at most 10,000 nodes in a case view. Actual: a larger recorded case. Suggestions: inspect the AEF results directly. Because: the viewer must not silently truncate evidence.");
        var nodes = lines.Select(line =>
        {
            var node = Node(line);
            var captured = node.ParentResultId is null ? WorkspaceRunProjector.CapturedLegacyTree(run, node.ResultId) : null;
            return node with { CapturedLegacyJson = captured is null ? null : StoreV1EvalTree.Serialize(captured) };
        }).ToArray();
        var evidenceIds = nodes.SelectMany(n => n.EvidenceIds).ToHashSet(StringComparer.Ordinal);
        return new(run.SourceId, run.Evidence.Info.RunId, caseId, resultId,
            Text(run.Evidence.Header, "contentCapture")!, run.Evidence.Info,
            nodes.Where(n => n.ParentResultId is null).Select(n => n.ResultId).ToArray(),
            Array(At(run.Evidence.Metrics, "metrics")).Select(Metric).ToArray(), nodes,
            WorkspaceArtifactProjector.Artifacts(run).Where(a => evidenceIds.Contains(a.EvidenceId)).ToArray());
    }

    internal static WorkspaceResultNode Node(JsonElement line)
    {
        var trials = At(line, "trials"); var component = At(line, "component");
        var aggregation = At(line, "aggregation"); var annotator = At(line, "annotator");
        var rule = At(line, "verdictRule");
        return new(Text(line, "resultId")!, Text(line, "parentResultId"), Text(line, "caseId")!, Text(line, "path")!,
            (int?)Integer(line, "trial"), Text(line, "evaluator", "id")!, Text(line, "evaluator", "version"),
            Text(line, "state")!, Text(line, "reason"), Text(line, "severity"),
            Text(line, "startedAt"), Text(line, "endedAt"), Number(line, "durationMs"),
            Array(At(line, "scores")).Select(s => new WorkspaceResultScore(Text(s, "metric")!, Number(s, "value")!.Value,
                Number(s, "normalized"), Text(s, "label"))).ToArray(),
            trials is null ? null : new(Integer(trials, "n")!.Value, Integer(trials, "passed")!.Value,
                Text(trials, "aggregation")!, Boolean(trials, "agree")!.Value, Integer(trials, "k")),
            component is null ? null : new(Number(component, "weight")!.Value, Boolean(component, "required")!.Value),
            aggregation is null ? null : new(Text(aggregation, "strategy")!, Text(aggregation, "rulePath")!,
                Number(aggregation, "score"), Number(aggregation, "threshold"), Integer(aggregation, "measured")!.Value,
                Integer(aggregation, "total")!.Value, Number(aggregation, "minimumMeasuredShare"), Strings(At(aggregation, "decisive")),
                At(aggregation, "unmeasured") is { ValueKind: JsonValueKind.Object } absent
                    ? AbsenceStates.Where(state => At(absent, state) is not null)
                        .Select(state => new WorkspaceAbsenceCount(state, Integer(absent, state)!.Value)).ToArray() : []),
            annotator is null ? null : new(Text(annotator, "kind")!, Text(annotator, "model"), Text(annotator, "promptHash"),
                Text(annotator, "rubricDigest"), Integer(annotator, "panel", "agree"), Integer(annotator, "panel", "of")),
            rule is null ? null : new(Text(rule, "expr")!, Number(rule, "threshold"), Text(rule, "source")),
            Array(At(line, "usage")).Select(u => new WorkspaceResultUsage(Text(u, "role")!, Text(u, "model"),
                Number(u, "costUsd"), Text(u, "costSource"), Integer(u, "gen_ai.usage.input_tokens"), Integer(u, "gen_ai.usage.output_tokens"),
                Integer(u, "gen_ai.usage.cache_read.input_tokens"), Integer(u, "gen_ai.usage.cache_write.input_tokens"),
                Integer(u, "gen_ai.usage.reasoning.output_tokens"))).ToArray(),
            Strings(At(line, "evidence")), line.GetRawText());
    }

    private static WorkspaceMetricDefinition Metric(JsonElement metric)
    {
        var scale = At(metric, "scale");
        return new(Text(metric, "id")!, Text(metric, "kind")!, Text(metric, "direction")!,
            scale is { ValueKind: JsonValueKind.Object } ? "bounded" : scale!.Value.GetString()!,
            Number(scale, "min"), Number(scale, "max"), Text(metric, "unit"), Text(metric, "description"), metric.GetRawText());
    }

    private static JsonElement? At(JsonElement? node, params string[] path)
    {
        foreach (var key in path)
        {
            if (node is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty(key, out var next)) return null;
            node = next;
        }
        return node is { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } ? null : node;
    }
    private static string? Text(JsonElement? node, params string[] path) => At(node, path) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    private static double? Number(JsonElement? node, params string[] path) => At(node, path) is { ValueKind: JsonValueKind.Number } value ? value.GetDouble() : null;
    // Verified AEF integers are integral binary64 values within +/- (2^53 - 1).
    // Their JSON spelling can contain a decimal point or exponent (encoding §2).
    private static long? Integer(JsonElement? node, params string[] path) => At(node, path) is { ValueKind: JsonValueKind.Number } value ? checked((long)value.GetDouble()) : null;
    private static bool? Boolean(JsonElement? node, params string[] path) => At(node, path) is { ValueKind: JsonValueKind.True or JsonValueKind.False } value ? value.GetBoolean() : null;
    private static IEnumerable<JsonElement> Array(JsonElement? node) => node is { ValueKind: JsonValueKind.Array } value ? value.EnumerateArray() : [];
    private static IReadOnlyList<string> Strings(JsonElement? node) => Array(node).Select(v => v.GetString()!).ToArray();
}
