// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.MissionControl.GraphQL;
using AgentEval.Results.Adapters.Reading;

namespace AgentEval.MissionControl.Services;

// Timeline projections only; verification/catalog selection and rendering stay in their existing layers.
internal static class WorkspaceTimelineProjector
{
    internal static IReadOnlyList<EvaluatorTimelinePoint> Points(IReadOnlyList<AefWorkspaceRun> runs, string key, int count) =>
        runs.SelectMany(run => Occurrences(run, key)).OrderByDescending(p => p.Timestamp)
            .ThenBy(p => p.SourceId, StringComparer.Ordinal).Take(count).ToArray();

    private static IEnumerable<EvaluatorTimelinePoint> Occurrences(AefWorkspaceRun run, string key)
    {
        var read = run.Evidence;
        var subject = WorkspaceRunProjector.Subject(read);
        var definitions = read.Metrics.GetProperty("metrics").EnumerateArray().Select(WorkspaceTreeProjector.Metric)
            .ToDictionary(m => m.Id, StringComparer.Ordinal);
        foreach (var raw in read.Results.Where(r => r.GetProperty("evaluator").GetProperty("id").GetString() == key))
        {
            var node = WorkspaceTreeProjector.Node(raw);
            var metrics = node.Scores.Select(s => definitions[s.MetricId]).ToArray();
            var normalized = node.Scores.Where(s => definitions[s.MetricId] is
                { Kind: "score", Direction: "higher_better", Scale: "bounded", Minimum: 0, Maximum: 1, Unit: null }).ToArray();
            var primary = normalized.Length == 1 ? normalized[0] : null;
            var confidence = raw.TryGetProperty("ext", out var ext) && ext.TryGetProperty("agenteval.store-v1", out var legacy)
                && legacy.TryGetProperty("confidence", out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble()
                : node.ParentResultId is null ? WorkspaceRunProjector.CapturedLegacyTree(run, node.ResultId)?.Score.Confidence : null;
            yield return new(read.Info.RunId, read.StartedAt, subject.Kind, subject.Name, primary?.Value,
                node.State == "passed" ? true : node.State == "failed" ? false : null, node.Severity, confidence,
                WorkspaceRunProjector.Run(run).ContentHash)
            {
                SourceId = run.SourceId, ResultId = node.ResultId, CaseId = node.CaseId, Path = node.Path, Trial = node.Trial,
                EvaluatorVersion = node.EvaluatorVersion, SubjectRef = subject.Ref, State = node.State,
                ScoreMetricId = primary?.MetricId, Threshold = node.VerdictRule?.Threshold,
                Scores = node.Scores, Metrics = metrics, Evidence = read.Info, RecordedJson = node.RecordedJson
            };
        }
    }
}
