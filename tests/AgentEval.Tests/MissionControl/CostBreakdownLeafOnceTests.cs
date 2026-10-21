// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER

using System.Net.Http.Json;
using System.Text.Json;
using AgentEval.Evals;
using AgentEval.MissionControl.GraphQL;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.MissionControl;

/// <summary>
/// T83: the run cost breakdown counts each leaf's cost once. A composite carries its parts' rolled-up cost
/// (<see cref="CostRollup"/>, as <c>CompositeEval</c> and the judge panels build it), and the walk added it beside its
/// leaves, so every leaf was counted once more for each composite above it. And while a leaf was not priced the total
/// was the priced part with nothing in the GraphQL shape saying so beyond a count: <c>isComplete</c> says it.
/// </summary>
public sealed class CostBreakdownLeafOnceTests : IClassFixture<SeededMissionControlFactory>
{
    private readonly SeededMissionControlFactory _factory;

    public CostBreakdownLeafOnceTests(SeededMissionControlFactory factory) => _factory = factory;

    private static EvalResult Leaf(string key, double? cost) => new(
        Metric: new EvalMetadata(key, key, "test", "1.0.0"),
        Score: new EvalScore(0.9, null, "pass", true, null, "none", null),
        Details: new EvalDetails(null, null, null, null, null),
        Provenance: new EvalProvenance("atomic-llm", "judge", null, null, null, cost, false),
        EvaluatedAt: DateTimeOffset.UtcNow);

    // A composite as CompositeEval builds one: its cost is its parts' rolled up, complete or absent.
    private static EvalResult Composite(string key, params EvalResult[] parts) => new(
        Metric: new EvalMetadata(key, key, "composite", "1.0.0"),
        Score: new EvalScore(0.9, null, "pass", true, null, "none", null),
        Details: new EvalDetails(null, null, null, parts, "weighted-sum"),
        Provenance: new EvalProvenance("composite", null, null, null, null, CostRollup.Aggregate(parts).EstimatedCost, false),
        EvaluatedAt: DateTimeOffset.UtcNow);

    // `depth` composites above one leaf, so the leaf sits at that depth.
    private static EvalResult Chain(int depth, EvalResult leaf)
    {
        var node = leaf;
        for (var level = 1; level <= depth; level++)
            node = Composite($"level-{level}", node);
        return node;
    }

    private static ScenarioResult Flat(string id, double? cost) => new(
        Id: id, Name: id, Input: "in", Output: "plain agent text", Passed: true, Score: 0.9,
        Metrics: new Dictionary<string, double>(), Assertions: Array.Empty<AssertionResult>(),
        Duration: TimeSpan.FromMilliseconds(100), EstimatedCost: cost);

    private static (double Total, double Unknown, int Unpriced) Walk(EvalResult tree)
    {
        double trivial = 0, low = 0, medium = 0, high = 0, unknown = 0;
        int unpriced = 0;
        Query.WalkAndAccumulate(tree, ref trivial, ref low, ref medium, ref high, ref unknown, ref unpriced);
        return (trivial + low + medium + high + unknown, unknown, unpriced);
    }

    [Fact]
    public void RolledUpComposites_AreWalkedNotAdded_EachLeafCountedOnce()
    {
        // root (0.006) → leaf-a (0.001), mid (0.005) → leaf-b (0.002), leaf-c (0.003)
        var tree = Composite("root", Leaf("leaf-a", 0.001), Composite("mid", Leaf("leaf-b", 0.002), Leaf("leaf-c", 0.003)));
        Assert.Equal(0.006, tree.Provenance.EstimatedCost!.Value, precision: 10);   // the shape under test

        var (total, _, unpriced) = Walk(tree);

        // Before T83 root's 0.006 and mid's 0.005 were added beside the leaves: 0.017, nearly three times the spend.
        Assert.Equal(0.006, total, precision: 10);
        Assert.Equal(0, unpriced);
    }

    [Fact]
    public void PastTheDepthCap_TheRolledUpCostStandsForTheSubtree_Once()
    {
        // The leaf ($0.5) sits past the cap. The composite at the cap carries its rollup and stands for it, once. Before
        // T83 every level down to the cap added the same $0.5: 33 times.
        var tree = Chain(depth: Query.MaxTreeWalkDepth + 8, Leaf("bottom", 0.5));

        var (total, unknown, unpriced) = Walk(tree);

        Assert.Equal(0.5, total, precision: 10);
        Assert.Equal(0.5, unknown, precision: 10);   // "level-8" is not in EvaluatorCostMap
        Assert.Equal(0, unpriced);
    }

    [Fact]
    public void PastTheDepthCap_AnUnpricedSubtree_CountsOnceAsUnpriced()
    {
        // The leaf past the cap was not priced, so every rollup above it is null. Before T83 nothing counted it: the
        // composites were skipped as composites and the leaf was never reached, and the breakdown read as complete.
        var tree = Chain(depth: Query.MaxTreeWalkDepth + 8, Leaf("bottom", null));

        var (total, _, unpriced) = Walk(tree);

        Assert.Equal(0.0, total);
        Assert.Equal(1, unpriced);
    }

    [Fact]
    public async Task Resolver_CountsEachLeafOnce_AndSaysTheTotalIsPartial()
    {
        // root (not priced) → mid (0.003) → leaf-a (0.001), leaf-b (0.002); leaf-unpriced (null). Plus a flat scenario at
        // 0.004. Before T83 mid's 0.003 was added beside its leaves (unknown 0.006, total 0.010).
        var tree = Composite("root",
            Composite("mid", Leaf("leaf-a", 0.001), Leaf("leaf-b", 0.002)),
            Leaf("leaf-unpriced", null));
        var (store, runId) = await SeedAsync(tree, Flat("flat", 0.004));

        var breakdown = await new Query().RunCostBreakdown(store, runId);

        Assert.NotNull(breakdown);
        Assert.Equal(0.003, breakdown!.UnknownKeyCost, precision: 10);
        Assert.Equal(0.004, breakdown.LegacyFlatCost, precision: 10);
        Assert.Equal(0.007, breakdown.TotalCost, precision: 10);   // the priced part ...
        Assert.Equal(1, breakdown.UnpricedCount);
        Assert.False(breakdown.IsComplete);                        // ... and it says so
    }

    [Fact]
    public async Task Resolver_EveryLeafPriced_TotalIsTheRunsRolledUpCost_AndComplete()
    {
        var tree = Composite("root", Leaf("leaf-a", 0.001), Composite("mid", Leaf("leaf-b", 0.002), Leaf("leaf-c", 0.003)));
        var (store, runId) = await SeedAsync(tree, Flat("flat", 0.004));

        var breakdown = await new Query().RunCostBreakdown(store, runId);

        Assert.NotNull(breakdown);
        // The breakdown agrees with the cost the run itself rolled up: the tree's root plus the flat scenario.
        Assert.Equal(tree.Provenance.EstimatedCost!.Value + 0.004, breakdown!.TotalCost, precision: 10);
        Assert.Equal(0, breakdown.UnpricedCount);
        Assert.True(breakdown.IsComplete);
    }

    [Fact]
    public async Task IsComplete_IsAGraphQLField_TheSpaCanRead()
    {
        // The seeded run prices every leaf and flat scenario, so it is complete.
        using var client = _factory.CreateClient();
        var listResp = await client.PostAsJsonAsync("/graphql", new { query = "{ recentRuns(count: 1) { runId } }" });
        listResp.EnsureSuccessStatusCode();
        using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        var runId = listDoc.RootElement.GetProperty("data").GetProperty("recentRuns")[0].GetProperty("runId").GetString();

        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = $@"{{ runCostBreakdown(runId: ""{runId}"") {{ totalCost unpricedCount isComplete }} }}",
        });
        var body = await resp.Content.ReadAsStringAsync();
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(body);
        var breakdown = doc.RootElement.GetProperty("data").GetProperty("runCostBreakdown");

        Assert.Equal(0, breakdown.GetProperty("unpricedCount").GetInt32());
        Assert.True(breakdown.GetProperty("isComplete").GetBoolean(), body);
    }

    private static async Task<(InMemoryOutputStore Store, string RunId)> SeedAsync(EvalResult tree, ScenarioResult flat)
    {
        var store = new InMemoryOutputStore();
        var manifest = await store.StartRunAsync(new SubjectIdentity(SubjectKind.Agent, "CostAgent"), new RunContext(
            EvalProject: "TestProject",
            EvalProjectPath: "samples/Test",
            Harness: "xunit",
            Seed: 1,
            ParentInvocationId: null,
            Kind: "eval"));
        var runId = manifest.Run.RunId;
        await store.WriteScenarioResultAsync(runId, EvalResultPersistence.ToScenarioResult(tree, "tree-scenario", "Composite tree"));
        await store.WriteScenarioResultAsync(runId, flat);
        return (store, runId);
    }
}

#endif
