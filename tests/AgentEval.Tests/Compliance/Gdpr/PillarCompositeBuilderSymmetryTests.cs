// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Compliance.Gdpr;

/// <summary>
/// Phase-8 T2.4 symmetry contract test.
/// <para>
/// GDPR <see cref="AgentEval.Compliance.Gdpr.Pillars.PillarCompositeBuilder"/> and
/// EU AI Act <see cref="AgentEval.Compliance.EuAiAct.Pillars.PillarCompositeBuilder"/>
/// must accept the same parameter shape on their <c>Build</c> method so callers can
/// inject a custom <see cref="IAggregationStrategy"/> on either regulation without
/// branching. The reflection assertion below pins the contract — adding a parameter
/// to one without updating the other fails the test loudly.
/// </para>
/// </summary>
public class PillarCompositeBuilderSymmetryTests
{
    [Fact]
    public void Build_Signatures_AreSymmetricAcrossRegulations()
    {
        var gdpr = typeof(AgentEval.Compliance.Gdpr.Pillars.PillarCompositeBuilder)
            .GetMethod("Build", BindingFlags.Public | BindingFlags.Static);
        var euai = typeof(AgentEval.Compliance.EuAiAct.Pillars.PillarCompositeBuilder)
            .GetMethod("Build", BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(gdpr);
        Assert.NotNull(euai);

        var gdprParams = gdpr!.GetParameters();
        var euaiParams = euai!.GetParameters();

        Assert.Equal(euaiParams.Length, gdprParams.Length);

        for (int i = 0; i < euaiParams.Length; i++)
        {
            // Same parameter name + same type + same "has default" semantics.
            // We compare types via ToString() so generic tuple shapes match.
            Assert.Equal(euaiParams[i].Name, gdprParams[i].Name);
            Assert.Equal(euaiParams[i].ParameterType.ToString(), gdprParams[i].ParameterType.ToString());
            Assert.Equal(euaiParams[i].IsOptional, gdprParams[i].IsOptional);
        }

        // Explicit: the 4th parameter must be an IAggregationStrategy? optional.
        Assert.Equal(4, gdprParams.Length);
        Assert.Equal("aggregation", gdprParams[3].Name);
        Assert.Equal(typeof(IAggregationStrategy), gdprParams[3].ParameterType);
        Assert.True(gdprParams[3].IsOptional);
    }

    [Fact]
    public void Build_DefaultAggregation_IsWeightedSum()
    {
        // Behavioural pin: the default aggregation remains WeightedSum so existing callers
        // (that pass only 3 args) get the historical behaviour unchanged.
        var article = new CompositeEval(
            key: "gdpr.art1",
            name: "art1",
            category: "compliance.gdpr.test",
            version: "1.0.0",
            components: [new EvalComponent(new StubLeaf(), 1.0, Required: true)],
            aggregation: WeightedSumAggregation.Instance,
            threshold: null);

        var pillar = AgentEval.Compliance.Gdpr.Pillars.PillarCompositeBuilder.Build(
            pillarKey: "Pillar1-Test",
            pillarName: "Test",
            articles: [(article, 1.0)]);

        Assert.Equal("WeightedSum", pillar.Aggregation.Name);
    }

    [Fact]
    public void Build_CustomAggregation_IsHonoured()
    {
        var article = new CompositeEval(
            key: "gdpr.art1",
            name: "art1",
            category: "compliance.gdpr.test",
            version: "1.0.0",
            components: [new EvalComponent(new StubLeaf(), 1.0, Required: true)],
            aggregation: WeightedSumAggregation.Instance,
            threshold: null);

        var pillar = AgentEval.Compliance.Gdpr.Pillars.PillarCompositeBuilder.Build(
            pillarKey: "Pillar1-Test",
            pillarName: "Test",
            articles: [(article, 1.0)],
            aggregation: CapByWorstAggregation.Instance);

        Assert.Equal("CapByWorst", pillar.Aggregation.Name);
    }

    private sealed class StubLeaf : IEval
    {
        public string Key => "stub.leaf";
        public string Name => "stub";
        public string Category => "compliance.test";
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
