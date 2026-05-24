// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using System.Text.Json;
using AgentEval.Core;
using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Agentic.Adjudication;

/// <summary>
/// Phase-8 T2.10 — regression tests for the canonical name each <see cref="IAggregationStrategy"/>
/// emits into <c>EvalResult.Details.AggregationStrategy</c> (which serialises to the JSON
/// <c>aggregationStrategy</c> field in <c>eval-result.schema.json</c>).
/// <para>
/// The aggregation-strategy <c>Name</c> is the canonical "agreement method" that downstream
/// dashboards, audit reports and Mission Control widgets pivot on. A drift in the string here
/// silently re-labels every historical run, so we pin the value per strategy + add a
/// reflection-based drift guard that fires when a new strategy is added without explicit pinning.
/// </para>
/// <para>
/// Test name shape uses the playbook's lower-case-with-hyphen wording (e.g.
/// <c>MajorityVote_SerialisesAs_majorityvote</c>) for trace clarity; the assertion pins
/// the codebase's PascalCase reality (e.g. <c>"MajorityVote"</c>). Changing the string is a
/// breaking change for downstream consumers — update consumers first, then this test.
/// </para>
/// </summary>
public class AgreementMethodSerialisationTests
{
    // ── Per-strategy pinning ──────────────────────────────────────────────────

    [Fact]
    public void WeightedSum_SerialisesAs_weightedsum()
    {
        Assert.Equal("WeightedSum", WeightedSumAggregation.Instance.Name);
        AssertCompositeEmitsName(WeightedSumAggregation.Instance);
    }

    [Fact]
    public void CapByWorst_SerialisesAs_capbyworst()
    {
        Assert.Equal("CapByWorst", CapByWorstAggregation.Instance.Name);
        AssertCompositeEmitsName(CapByWorstAggregation.Instance);
    }

    [Fact]
    public void MajorityVote_SerialisesAs_majorityvote()
    {
        Assert.Equal("MajorityVote", MajorityVoteAggregation.Instance.Name);
        AssertCompositeEmitsName(MajorityVoteAggregation.Instance);
    }

    [Fact]
    public void WeightedMedian_SerialisesAs_weightedmedian()
    {
        Assert.Equal("WeightedMedian", WeightedMedianAggregation.Instance.Name);
        AssertCompositeEmitsName(WeightedMedianAggregation.Instance);
    }

    [Fact]
    public void Min_SerialisesAs_min()
    {
        Assert.Equal("Min", MinAggregation.Instance.Name);
        AssertCompositeEmitsName(MinAggregation.Instance);
    }

    // ── JSON-round-trip pin ───────────────────────────────────────────────────

    [Fact]
    public void AggregationStrategy_RoundTripsThroughJson()
    {
        // The on-disk JSON field name is `aggregationStrategy` (per eval-result.schema.json:54).
        // Serialise with the default camelCase policy and assert the wire shape.
        var strategy = CapByWorstAggregation.Instance;
        var details = new EvalDetails(
            Dimensions: null,
            Evidence: null,
            Recommendations: null,
            SubResults: null,
            AggregationStrategy: strategy.Name);

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(details, options);

        Assert.Contains("\"aggregationStrategy\":\"CapByWorst\"", json);

        // Round-trip back through the deserializer pins both sides of the contract.
        var restored = JsonSerializer.Deserialize<EvalDetails>(json, options);
        Assert.NotNull(restored);
        Assert.Equal(strategy.Name, restored!.AggregationStrategy);
    }

    // ── Drift guard ───────────────────────────────────────────────────────────

    [Fact]
    public void AllAggregationStrategies_HaveCanonicalPinnedName()
    {
        // Enumerate every public IAggregationStrategy implementor in the Core assembly.
        // For each, assert that its `Name` is non-empty AND that we have a pinning test
        // for it above (by checking the expected-name map). Adding a new strategy
        // without updating this map breaks the test loudly.
        var pinned = new Dictionary<Type, string>
        {
            [typeof(WeightedSumAggregation)]    = "WeightedSum",
            [typeof(CapByWorstAggregation)]     = "CapByWorst",
            [typeof(MajorityVoteAggregation)]   = "MajorityVote",
            [typeof(WeightedMedianAggregation)] = "WeightedMedian",
            [typeof(MinAggregation)]            = "Min",
        };

        var coreAsm = typeof(WeightedSumAggregation).Assembly;
        var discovered = coreAsm.GetTypes()
            .Where(t => !t.IsAbstract
                        && !t.IsInterface
                        && t.IsPublic
                        && typeof(IAggregationStrategy).IsAssignableFrom(t))
            .ToList();

        Assert.NotEmpty(discovered);

        var missingPin = new List<string>();
        var nameMismatch = new List<string>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var type in discovered)
        {
            // Each strategy exposes a static `Instance` of itself (singleton pattern).
            var instanceProp = type.GetProperty(
                "Instance",
                BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(instanceProp);
            var instance = (IAggregationStrategy)instanceProp!.GetValue(null)!;
            Assert.NotNull(instance);

            // Name must be non-empty AND unique across the family.
            Assert.False(string.IsNullOrWhiteSpace(instance.Name),
                $"{type.FullName}.Name must be non-empty.");
            Assert.True(seenNames.Add(instance.Name),
                $"Duplicate aggregation-strategy name '{instance.Name}' — names must be unique to avoid silently mis-labeling historical runs.");

            if (!pinned.TryGetValue(type, out var expected))
                missingPin.Add(type.FullName ?? type.Name);
            else if (instance.Name != expected)
                nameMismatch.Add($"{type.FullName}: expected '{expected}', got '{instance.Name}'");
        }

        Assert.True(missingPin.Count == 0,
            "New IAggregationStrategy implementations are missing from the pinning map in " +
            $"AgreementMethodSerialisationTests. Add a per-strategy test + extend the `pinned` map: {string.Join(", ", missingPin)}");
        Assert.True(nameMismatch.Count == 0,
            "Aggregation-strategy Name drifted from its pinned canonical value: " +
            string.Join("; ", nameMismatch));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a one-component composite around <paramref name="strategy"/>, evaluates it,
    /// and asserts the result's <c>AggregationStrategy</c> matches <c>strategy.Name</c>.
    /// </summary>
    private static void AssertCompositeEmitsName(IAggregationStrategy strategy)
    {
        var composite = new CompositeEval(
            key: "test.agg",
            name: "agg-test",
            category: "test",
            version: "1.0.0",
            components: [new EvalComponent(new ConstLeaf(), 1.0, Required: true)],
            aggregation: strategy,
            threshold: null);

        var result = composite.EvaluateAsync(new EvalInput(Query: "q", Response: "r")).GetAwaiter().GetResult();
        Assert.Equal(strategy.Name, result.Details.AggregationStrategy);
    }

    private sealed class ConstLeaf : IEval
    {
        public string Key => "test.leaf";
        public string Name => "leaf";
        public string Category => "test";
        public string Version => "1.0.0";

        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
            Task.FromResult(new EvalResult(
                Metric: new(Key, Name, Category, Version),
                Score: new(1.0, null, "pass", true, 0.5, "none", null),
                Details: new(null, null, null, null, null),
                Provenance: new("atomic", null, null, null, null, 0, false),
                EvaluatedAt: DateTimeOffset.UtcNow));
    }
}
