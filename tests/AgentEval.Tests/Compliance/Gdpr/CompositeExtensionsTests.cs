// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Compliance.Core;
using Xunit;

namespace AgentEval.Tests.Compliance.Gdpr;

public class CompositeExtensionsTests
{
    // ── Stub ──────────────────────────────────────────────────────────────────

    private sealed class StubAtomic(string key)
        : AtomicEval(key, key, "test", "1.0.0")
    {
        public override Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
            Task.FromResult(new EvalResult(
                Metric: new(Key, Name, Category, Version),
                Score: new(1.0, null, "pass", true, null, "none", null),
                Details: new(null, null, null, null, null),
                Provenance: new("atomic-code", null, null, null, null, 0.001, false),
                EvaluatedAt: DateTimeOffset.UtcNow));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static EvalComponent MakeComponent(string key, double weight = 1.0) =>
        new(new StubAtomic(key), weight, Required: true);

    private static CompositeEval MakeArticle(string controlId, params (string key, double weight)[] scenarios)
    {
        var components = scenarios
            .Select(s => new EvalComponent(new StubAtomic(s.key), s.weight, Required: true))
            .ToList();

        return new CompositeEval(
            key: controlId,
            name: controlId,
            category: "test",
            version: "1.0.0",
            components: components,
            aggregation: WeightedSumAggregation.Instance,
            threshold: 0.85);
    }

    private static CompositeEval MakeBenchmark(params CompositeEval[] articles)
    {
        var components = articles
            .Select(a => new EvalComponent(a, 1.0 / articles.Length, Required: true))
            .ToList();

        return new CompositeEval(
            key: "benchmark",
            name: "Benchmark",
            category: "test",
            version: "1.0.0",
            components: components,
            aggregation: WeightedSumAggregation.Instance,
            threshold: 0.85);
    }

    // ── Null-guard tests ──────────────────────────────────────────────────────

    [Fact]
    public void WithExtraScenarios_NullBenchmark_Throws()
    {
        // Arrange
        CompositeEval nullBenchmark = null!;
        var additions = new Dictionary<string, IReadOnlyList<EvalComponent>>();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => nullBenchmark.WithExtraScenarios(additions));
    }

    [Fact]
    public void WithExtraScenarios_NullAdditions_Throws()
    {
        // Arrange
        var benchmark = MakeBenchmark(MakeArticle("gdpr.art9", ("s1", 1.0)));
        IReadOnlyDictionary<string, IReadOnlyList<EvalComponent>> nullAdditions = null!;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => benchmark.WithExtraScenarios(nullAdditions));
    }

    [Fact]
    public void WithExtraScenarios_EmptyAdditions_ReturnsOriginal()
    {
        // Arrange
        var benchmark = MakeBenchmark(MakeArticle("gdpr.art9", ("s1", 1.0)));
        var emptyAdditions = new Dictionary<string, IReadOnlyList<EvalComponent>>();

        // Act
        var result = benchmark.WithExtraScenarios(emptyAdditions);

        // Assert
        Assert.Same(benchmark, result);
    }

    [Fact]
    public void WithExtraScenarios_AppendsToTargetedArticle()
    {
        // Arrange
        var art9 = MakeArticle("gdpr.art9", ("s1", 1.0), ("s2", 1.0));
        var art17 = MakeArticle("gdpr.art17", ("s3", 1.0));
        var benchmark = MakeBenchmark(art9, art17);

        var extras = new List<EvalComponent>
        {
            MakeComponent("extra1"),
            MakeComponent("extra2")
        };
        var additions = new Dictionary<string, IReadOnlyList<EvalComponent>>
        {
            ["gdpr.art9"] = extras
        };
        var originalArt9ComponentCount = art9.Components.Count;

        // Act
        var result = benchmark.WithExtraScenarios(additions);

        // Assert — find the augmented art9 in the result tree
        var augmentedArt9 = result.Components
            .Select(c => c.Eval)
            .OfType<CompositeEval>()
            .Single(c => c.Key == "gdpr.art9");

        var untouchedArt17 = result.Components
            .Select(c => c.Eval)
            .OfType<CompositeEval>()
            .Single(c => c.Key == "gdpr.art17");

        Assert.Equal(originalArt9ComponentCount + 2, augmentedArt9.Components.Count);
        Assert.Equal(art17.Components.Count, untouchedArt17.Components.Count);
    }

    [Fact]
    public void WithExtraScenarios_RenormalisesWeights()
    {
        // Arrange
        var art9 = MakeArticle("gdpr.art9", ("s1", 0.5), ("s2", 0.5));
        var benchmark = MakeBenchmark(art9);

        var extras = new List<EvalComponent>
        {
            MakeComponent("extra1", weight: 1.0),
            MakeComponent("extra2", weight: 1.0)
        };
        var additions = new Dictionary<string, IReadOnlyList<EvalComponent>>
        {
            ["gdpr.art9"] = extras
        };

        // Act
        var result = benchmark.WithExtraScenarios(additions);

        var augmentedArt9 = result.Components
            .Select(c => c.Eval)
            .OfType<CompositeEval>()
            .Single(c => c.Key == "gdpr.art9");

        // Assert — weights must sum to 1.0 after renormalisation
        var weightSum = augmentedArt9.Components.Sum(c => c.Weight);
        Assert.Equal(1.0, weightSum, precision: 10);
    }

    [Fact]
    public void WithExtraScenarios_NestedComposite_RecursesIntoSubtree()
    {
        // Arrange — pillar → article hierarchy (2 levels deep)
        var art9 = MakeArticle("gdpr.art9", ("s1", 1.0));
        var pillar = new CompositeEval(
            key: "pillar1",
            name: "Pillar 1",
            category: "test",
            version: "1.0.0",
            components: [new EvalComponent(art9, 1.0, Required: true)],
            aggregation: WeightedSumAggregation.Instance,
            threshold: 0.85);

        var benchmark = MakeBenchmark(pillar);

        var extras = new List<EvalComponent>
        {
            MakeComponent("extra1"),
            MakeComponent("extra2")
        };
        var additions = new Dictionary<string, IReadOnlyList<EvalComponent>>
        {
            ["gdpr.art9"] = extras
        };

        // Act
        var result = benchmark.WithExtraScenarios(additions);

        // Assert — navigate pillar → article
        var resultPillar = result.Components
            .Select(c => c.Eval)
            .OfType<CompositeEval>()
            .Single(c => c.Key == "pillar1");

        var resultArt9 = resultPillar.Components
            .Select(c => c.Eval)
            .OfType<CompositeEval>()
            .Single(c => c.Key == "gdpr.art9");

        Assert.Equal(3, resultArt9.Components.Count); // 1 original + 2 extras
    }

    [Fact]
    public void WithExtraScenarios_NonExistentControlId_NoOpForThatKey()
    {
        // Arrange
        var art9 = MakeArticle("gdpr.art9", ("s1", 1.0), ("s2", 1.0));
        var benchmark = MakeBenchmark(art9);

        var extras = new List<EvalComponent> { MakeComponent("extra1") };
        var additions = new Dictionary<string, IReadOnlyList<EvalComponent>>
        {
            ["gdpr.fake"] = extras   // This key doesn't exist in the tree
        };

        // Act
        var result = benchmark.WithExtraScenarios(additions);

        // Assert — art9 is unchanged
        var resultArt9 = result.Components
            .Select(c => c.Eval)
            .OfType<CompositeEval>()
            .Single(c => c.Key == "gdpr.art9");

        Assert.Equal(2, resultArt9.Components.Count);
    }
}
