// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

#if NET10_0_OR_GREATER
using System.Text.Json;
using AgentEval.MissionControl.GraphQL;
using AgentEval.MissionControl.Services;
using AgentEval.Results.Adapters.Reading;

namespace AgentEval.Tests.Cli.Aef;

// Actual producer documents -> existing resolvers. Catalog discovery/selection is verified separately.
internal static class MissionControlProducerAssertions
{
    internal static async Task<IReadOnlyList<WorkspaceScenario>> ReadAsync(string directory, string? singleScenarioName = null)
    {
        var read = new AefRunEvidenceReader().Read(directory);
        var source = new SnapshotSource(new("source-producer-proof", read, []));
        var query = new Query();
        var view = await query.Run(source, read.Info.RunId, "source-producer-proof");
        Assert.NotNull(view);
        Assert.Equal(read.Info.SubjectRef, view.Subject.Ref);
        Assert.Equal(read.Info.RunId, view.Run.RunId);
        Assert.Equal(read.StartedAt, view.Run.Timestamp);
        Assert.Equal(read.Duration, view.Run.Duration);
        Assert.Equal(read.Info.Integrity, view.Evidence.Integrity);
        var roots = read.Results.Where(r => Text(r, "parentResultId") is null && !r.TryGetProperty("trial", out _)).ToArray();
        var scenarios = await query.Scenarios(source, read.Info.RunId, "source-producer-proof");
        Assert.Equal(roots.Length, scenarios.Count);
        if (!read.Info.Imported) Assert.Equal(roots.Length, view.Run.ScenarioCount);
        if (singleScenarioName is not null) Assert.Single(roots);
        foreach (var row in roots)
        {
            var scenario = Assert.Single(scenarios, s => s.ResultId == Text(row, "resultId"));
            var id = Text(row, "caseId")!;
            var state = Text(row, "state");
            Assert.Equal(id, scenario.Id); // Canonical identity is never rewritten to a comparison alias.
            Assert.Equal(state, scenario.State);
            Assert.Equal(state == "passed" ? true : state == "failed" ? false : (bool?)null, scenario.Passed);
            var measured = state is "passed" or "failed" or "warn" or "scored" or "inconclusive";
            Assert.Equal(measured ? null : state == "not_applicable" ? "not_applicable" : "not_measured", scenario.Measurement);
            var score = Scores(row).Where(s => s.MetricId == "score").Select(s => (double?)s.Value).SingleOrDefault();
            Assert.Equal(measured ? score : null, scenario.Score);
            Assert.Equal(Number(row, "durationMs") is { } ms ? TimeSpan.FromMilliseconds(ms) : (TimeSpan?)null, scenario.Duration);
            var tree = await query.ScenarioResultTree(source, read.Info.RunId, id, "source-producer-proof", scenario.ResultId);
            Assert.NotNull(tree);
            var original = read.Results.Where(r => Text(r, "caseId") == id).ToArray();
            Assert.Equal(original.Select(r => Text(r, "resultId")).Order(StringComparer.Ordinal),
                tree.Nodes.Select(n => n.ResultId).Order(StringComparer.Ordinal));
            foreach (var node in tree.Nodes)
            {
                var raw = Assert.Single(original, r => Text(r, "resultId") == node.ResultId);
                Assert.Equal(Text(raw, "state"), node.State);
                Assert.Equal(Text(raw.GetProperty("evaluator"), "id"), node.EvaluatorId);
                Assert.Equal(Number(raw, "durationMs"), node.DurationMs);
                Assert.Equal(Scores(raw), node.Scores.Select(s => (s.MetricId, s.Value)));
                Assert.Equal(raw.GetRawText(), node.RecordedJson);
            }
            Assert.Equal(read.Metrics.GetProperty("metrics").EnumerateArray().Select(m => Text(m, "id")).Order(StringComparer.Ordinal),
                tree.Metrics.Select(m => m.Id).Order(StringComparer.Ordinal));
            string? recordedName = null;
            if (row.TryGetProperty("ext", out var ext))
            {
                if (ext.TryGetProperty("agenteval.store-v1", out var legacy)) recordedName = Text(legacy, "name");
                if (recordedName is null && ext.TryGetProperty("agenteval.comparison", out var comparison)
                    && Number(comparison, "version") == 1) recordedName = Text(comparison, "name");
            }
            Assert.Equal(singleScenarioName ?? recordedName ?? id, scenario.Name);
        }
        if (read.Results.Count > 0)
        {
            var key = Text(read.Results[0].GetProperty("evaluator"), "id")!;
            var original = read.Results.Where(r => Text(r.GetProperty("evaluator"), "id") == key).ToArray();
            Assert.InRange(original.Length, 1, 200);
            var timeline = await query.EvaluatorTimeline(source, key, 200);
            Assert.Equal(original.Select(r => Text(r, "resultId")).Order(StringComparer.Ordinal),
                timeline.Select(p => p.ResultId).Order(StringComparer.Ordinal));
            foreach (var point in timeline)
            {
                var raw = Assert.Single(original, r => Text(r, "resultId") == point.ResultId);
                Assert.Equal(read.StartedAt, point.Timestamp);
                Assert.Equal(Text(raw, "state"), point.State);
                Assert.Equal(Scores(raw), point.Scores.Select(s => (s.MetricId, s.Value)));
            }
        }
        return scenarios;
    }

    private static string? Text(JsonElement value, string key) => value.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static double? Number(JsonElement value, string key) => value.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    private static IEnumerable<(string MetricId, double Value)> Scores(JsonElement row) => row.TryGetProperty("scores", out var scores)
        ? scores.EnumerateArray().Select(s => (s.GetProperty("metric").GetString()!, s.GetProperty("value").GetDouble())) : [];
    private sealed class SnapshotSource(AefWorkspaceRun run) : IWorkspaceRunSource
    {
        public Task<IReadOnlyList<AefWorkspaceRun>> ReadAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AefWorkspaceRun>>([run]);
    }
}
#endif
