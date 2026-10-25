// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Evals;
using System.Text.Json.Nodes;
using AgentEval.Output;
using AgentEval.Results.Adapters.Comparison;
using AgentEval.Results.Adapters.StoreV1;
using AgentEval.Results.Integrity;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Tests.StoreV1;

public sealed class StoreV1TreeTests : IDisposable
{
    private readonly string _root = AefTestRuns.TempPath("store-tree");
    public void Dispose() => AefTestRuns.Delete(_root);

    [Theory]
    [InlineData(AefContentCapture.Off)]
    [InlineData(AefContentCapture.On)]
    public async Task Export_RecursiveEvaluators_PreservesCanonicalNodesWithoutDependingOnCapture(AefContentCapture capture)
    {
        using var fixture = await StoreV1Fixture.CreateAsync();
        var manifest = (await fixture.Store.GetRunManifestAsync(fixture.RunId))!;
        var absent = Node("duplicate/key", EvalScore.NotApplicable());
        var scored = Node("duplicate/key", new(0.65, null, "scored", false, null, "none", null));
        var branch = Node("branch", new(0.8, null, "pass", true, 0.5, "none", null), scored, absent);
        var tree = Node("root", new(0.8, null, "pass", true, 0.5, "none", null), branch);
        var scenario = EvalResultPersistence.ToScenarioResult(tree, "tree-case", "Tree", [AssertionResult.Pass("assertion")]);
        var conversion = StoreV1Exporter.Export(new(manifest, null, [scenario], null, []), _root,
            new() { ContentCapture = capture });
        Assert.Equal(AefOutcome.Intact, conversion.Verification.Outcome);
        var results = AefTestRuns.Results(_root);
        Assert.Equal(5, results.Count);
        var root = Assert.Single(results, r => (string?)r["path"] == "scenario");
        var middle = Assert.Single(results, r => (string?)r["path"] == "scenario/eval/1");
        var first = Assert.Single(results, r => (string?)r["path"] == "scenario/eval/1/1");
        var second = Assert.Single(results, r => (string?)r["path"] == "scenario/eval/1/2");
        Assert.Equal((string?)root["resultId"], (string?)middle["parentResultId"]);
        Assert.Equal((string?)middle["resultId"], (string?)first["parentResultId"]);
        Assert.Equal((string?)middle["resultId"], (string?)second["parentResultId"]);
        Assert.NotEqual((string?)first["resultId"], (string?)second["resultId"]);
        Assert.Equal("duplicate/key", (string?)first["evaluator"]!["id"]);
        Assert.Equal("scored", (string?)first["state"]);
        Assert.Equal(0.65, (double?)first["scores"]![0]!["value"]);
        Assert.Equal("not_applicable", (string?)second["state"]);
        Assert.Null(second["scores"]);
        Assert.Equal(2, (int?)root["aggregation"]!["total"]);
        Assert.Equal(2, (int?)middle["aggregation"]!["total"]);
        Assert.Equal("Own", (string?)middle["aggregation"]!["strategy"]);
        Assert.Null(middle["aggregation"]!["decisive"]);
        Assert.Equal(0, (double?)middle["component"]!["weight"]);
        var comparison = new AefComparisonReader().Read(_root);
        Assert.Single(comparison.Scenarios);
        Assert.Equal("tree-case", comparison.Scenarios[0].Id);
        Assert.Equal(0.8, comparison.Scenarios[0].Score);
        Assert.Equal(1, (int?)root["ext"]![StoreV1Exporter.ExtName]!["evalTreeProjectionVersion"]);
    }

    [Fact]
    public async Task Export_MixedRootScaleAndConflictingDimension_PreservesMetricMeanings()
    {
        using var fixture = await StoreV1Fixture.CreateAsync();
        var manifest = (await fixture.Store.GetRunManifestAsync(fixture.RunId))!;
        var child = Node("leaf", new(0.7, null, "pass", true, 0.5, "none", null));
        child = child with { Details = child.Details with { Dimensions = new Dictionary<string, double>
            { ["agenteval.eval.score"] = 43, ["latency"] = 900 } } };
        var tree = Node("root", new(0.8, null, "pass", true, 0.5, "none", null), child);
        var scenario = EvalResultPersistence.ToScenarioResult(tree, "tree", "Tree", []);
        ScenarioResult generic = new("generic", "Generic", "", "plain", true, 75, new Dictionary<string, double>(), [], null, null);
        StoreV1Exporter.Export(new(manifest, null, [scenario, generic], null, []), _root);
        var metrics = AefTestRuns.Document(_root, "metrics.json")["metrics"]!.AsArray();
        Assert.Equal("unbounded", (string?)Assert.Single(metrics, m => (string?)m!["id"] == "score")!["scale"]);
        var result = Assert.Single(AefTestRuns.Results(_root), r => (string?)r["path"] == "scenario/eval/1");
        var scores = result["scores"]!.AsArray();
        var normalized = Assert.Single(scores, s => (double?)s!["value"] == 0.7)!;
        var id = (string)normalized["metric"]!;
        Assert.NotEqual("agenteval.eval.score", id);
        var definition = Assert.Single(metrics, m => (string?)m!["id"] == id)!;
        Assert.Equal(0, (double?)definition["scale"]!["min"]);
        Assert.Equal(1, (double?)definition["scale"]!["max"]);
        Assert.Equal(43, (double?)Assert.Single(scores, s => (string?)s!["metric"] == "agenteval.eval.score")!["value"]);
        Assert.Equal(900, (double?)Assert.Single(scores, s => (string?)s!["metric"] == "latency")!["value"]);
        var comparison = new AefComparisonReader().Read(_root);
        Assert.Equal(2, comparison.Scenarios.Count);
        Assert.Equal(75, Assert.Single(comparison.Scenarios, s => s.Id == "generic").Score);
        Assert.Null(comparison.Metrics["generic"].Maximum);
    }

    [Theory]
    [InlineData("score", "value")]
    [InlineData("score", "passed")]
    [InlineData("provenance", "cacheHit")]
    public async Task Export_MissingDescendantFact_RefusesConstructorDefaults(string section, string property)
    {
        using var fixture = await StoreV1Fixture.CreateAsync();
        var manifest = (await fixture.Store.GetRunManifestAsync(fixture.RunId))!;
        var child = Node("leaf", new(0, null, "fail", false, 0.5, "low", null));
        var tree = Node("root", new(0.8, null, "pass", true, 0.5, "none", null), child);
        var scenario = EvalResultPersistence.ToScenarioResult(tree, "tree", "Tree", []);
        var raw = JsonNode.Parse(scenario.Output)!;
        raw["details"]!["subResults"]![0]![section]!.AsObject().Remove(property);
        scenario = scenario with { Output = raw.ToJsonString() };
        var error = Assert.Throws<InvalidDataException>(() =>
            StoreV1Exporter.Export(new(manifest, null, [scenario], null, []), _root));
        Assert.Contains("required", error.Message);
        Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData("depth")]
    [InlineData("count")]
    [InlineData("enum")]
    [InlineData("number")]
    [InlineData("duplicate")]
    [InlineData("duplicate-dimension")]
    [InlineData("null-child")]
    [InlineData("missing-score")]
    [InlineData("missing-metric")]
    [InlineData("missing-details")]
    [InlineData("missing-provenance")]
    public async Task Export_InvalidRecognizableTree_RefusesRatherThanFlattening(string defect)
    {
        using var fixture = await StoreV1Fixture.CreateAsync();
        var manifest = (await fixture.Store.GetRunManifestAsync(fixture.RunId))!;
        var tree = Node("root", new(0.8, null, "pass", true, 0.5, "none", null));
        var scenario = EvalResultPersistence.ToScenarioResult(tree, "tree", "Tree", []);
        var raw = JsonNode.Parse(scenario.Output)!;
        if (defect.StartsWith("missing-", StringComparison.Ordinal)) raw.AsObject().Remove(defect[8..]);
        if (defect == "depth")
            for (var i = 0; i < 34; i++)
            {
                var parent = JsonNode.Parse(scenario.Output)!;
                parent["details"]!["subResults"] = new JsonArray(raw);
                raw = parent;
            }
        if (defect == "count") raw["details"]!["subResults"] = new JsonArray(
            Enumerable.Range(0, 10_000).Select(_ => JsonNode.Parse(scenario.Output)).ToArray());
        if (defect == "enum") raw["score"]!["measurement"] = "unknown-measurement";
        if (defect == "number") raw["score"]!["value"] = "not-a-number";
        if (defect == "null-child") raw["details"]!["subResults"] = new JsonArray((JsonNode?)null);
        if (defect == "duplicate-dimension")
        {
            var child = JsonNode.Parse(scenario.Output)!;
            child["details"]!["dimensions"] = new JsonObject { ["latency"] = 1 };
            raw["details"]!["subResults"] = new JsonArray(child);
        }
        var output = raw.ToJsonString(new() { MaxDepth = 256 });
        if (defect == "duplicate") output = output.Replace("\"passed\":true", "\"passed\":false,\"passed\":true");
        if (defect == "duplicate-dimension")
        {
            output = output.Replace("\"latency\":1", "\"latency\":1,\"latency\":2");
            Assert.Contains("\"latency\":1,\"latency\":2", output);
        }
        scenario = scenario with { Output = output };
        Assert.Throws<InvalidDataException>(() => StoreV1Exporter.Export(new(manifest, null, [scenario], null, []), _root));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task Export_OrdinaryJsonWithMetricAndScore_RemainsFlatOutput()
    {
        using var fixture = await StoreV1Fixture.CreateAsync();
        var manifest = (await fixture.Store.GetRunManifestAsync(fixture.RunId))!;
        ScenarioResult scenario = new("generic", "Generic", "", "{\"metric\":\"latency\",\"score\":75,\"unit\":\"ms\"}",
            true, 75, new Dictionary<string, double>(), [], null, null);
        StoreV1Exporter.Export(new(manifest, null, [scenario], null, []), _root);
        var result = Assert.Single(AefTestRuns.Results(_root));
        Assert.Equal(75, (double?)result["scores"]![0]!["value"]);
        Assert.Null(result["ext"]![StoreV1Exporter.ExtName]!["evalTreeProjectionVersion"]);
    }

    [Theory]
    [InlineData("scored", "scored")]
    [InlineData("inconclusive", "inconclusive")]
    [InlineData("error", "error")]
    [InlineData("skipped", "skipped")]
    [InlineData("custom", "failed")]
    public async Task Export_DescendantState_PreservesStateAndAttemptedCost(string label, string expected)
    {
        using var fixture = await StoreV1Fixture.CreateAsync();
        var manifest = (await fixture.Store.GetRunManifestAsync(fixture.RunId))!;
        var child = Node("leaf", new(0.7, null, label, false, null, "none", null)) with
        {
            Provenance = new("llm-judge", "configured-model", null, null, 12, 0.01, false),
        };
        var tree = Node("root", new(0.8, null, "pass", true, 0.5, "none", null), child);
        var scenario = EvalResultPersistence.ToScenarioResult(tree, "tree", "Tree", []);
        StoreV1Exporter.Export(new(manifest, null, [scenario], null, []), _root);
        var result = Assert.Single(AefTestRuns.Results(_root), r => (string?)r["path"] == "scenario/eval/1");
        Assert.Equal(expected, (string?)result["state"]);
        Assert.Equal(0.01, (double?)result["usage"]![0]!["costUsd"]);
        if (expected is "error" or "skipped")
        {
            Assert.Null(result["scores"]);
            Assert.Null(result["annotator"]);
        }
    }

    [Fact]
    public async Task Export_CaptureModes_PreserveSameNumericHierarchyAndKeepNarrativeOutWhenOff()
    {
        using var fixture = await StoreV1Fixture.CreateAsync();
        var manifest = (await fixture.Store.GetRunManifestAsync(fixture.RunId))!;
        var leaf = Node("leaf", new(0.6, null, "pass", true, 0.5, "none", null));
        leaf = leaf with { Details = leaf.Details with { Summary = "PRIVATE-NARRATIVE" },
            Provenance = leaf.Provenance with { EstimatedCost = null } };
        var tree = Node("root", new(0.8, null, "pass", true, 0.5, "none", null), leaf);
        var scenario = EvalResultPersistence.ToScenarioResult(tree, "tree", "Tree", []);
        var off = Path.Combine(_root, "off");
        var on = Path.Combine(_root, "on");
        StoreV1Exporter.Export(new(manifest, null, [scenario], null, []), off, new() { ContentCapture = AefContentCapture.Off });
        StoreV1Exporter.Export(new(manifest, null, [scenario], null, []), on, new() { ContentCapture = AefContentCapture.On });
        var left = AefTestRuns.Results(off);
        var right = AefTestRuns.Results(on);
        Assert.Equal(left.Count, right.Count);
        for (var i = 0; i < left.Count; i++)
            foreach (var field in new[] { "resultId", "parentResultId", "caseId", "path", "evaluator", "state", "scores", "usage", "aggregation" })
                Assert.True(JsonNode.DeepEquals(left[i][field], right[i][field]), field);
        Assert.Null(left[1]["usage"]);
        Assert.DoesNotContain("PRIVATE-NARRATIVE", File.ReadAllText(Path.Combine(off, "results.ndjson")));
        Assert.False(File.Exists(Path.Combine(off, "evidence.ndjson")));
        Assert.Contains("PRIVATE-NARRATIVE", File.ReadAllText(Path.Combine(on, "results.ndjson")));
    }

    private static EvalResult Node(string key, EvalScore score, params EvalResult[] children) => new(
        new(key, key, "test", "1.0"), score,
        new(null, null, null, children.Length == 0 ? null : children, null),
        new("code", null, null, null, null, 0, false), DateTimeOffset.Parse("2026-10-23T10:00:00Z"));
}
