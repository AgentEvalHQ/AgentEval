// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.RedTeam;
using AgentEval.RedTeam.Evaluators;
using AgentEval.RedTeam.Transforms;

namespace AgentEval.Tests.RedTeam.Transforms;

public class TransformedAttackTests
{
    private sealed class FakeAttack : IAttackType
    {
        public string Name => "Fake";
        public string DisplayName => "Fake Attack";
        public string Description => "test seed attack";
        public string OwaspLlmId => "LLM01";
        public string[] MitreAtlasIds => ["AML.T0051"];
        public Severity DefaultSeverity => Severity.Medium;
        public IProbeEvaluator GetEvaluator() => new ContainsTokenEvaluator("PWNED");
        public IReadOnlyList<AttackProbe> GetProbes(Intensity intensity) =>
            Enumerable.Range(1, 3).Select(i => new AttackProbe
            {
                Id = $"F-{i:D3}", Prompt = "PWNED", Difficulty = Difficulty.Easy,
                AttackName = "Fake", Technique = "direct", ExpectedTokens = ["PWNED"],
            }).ToList();
    }

    [Fact]
    public void Expand_FansOut_OneSiblingPerTransformer()
    {
        var ta = new TransformedAttack(new FakeAttack(), [new Base64Transformer(), new HexTransformer()], TransformMode.Expand);
        var probes = ta.GetProbes(Intensity.Quick);

        Assert.Equal(6, probes.Count);                                                  // 3 seeds × 2 transformers
        Assert.All(probes, p => Assert.DoesNotContain("PWNED", p.Prompt, StringComparison.Ordinal));
        Assert.All(probes, p => Assert.Contains("PWNED", p.ExpectedTokens!));            // evaluator still fires
        Assert.Equal(probes.Count, probes.Select(p => p.Id).Distinct().Count());        // unique ids
    }

    [Fact]
    public void Expand_KeepOriginal_AlsoEmitsSeeds()
    {
        var ta = new TransformedAttack(new FakeAttack(), [new Base64Transformer()], TransformMode.Expand, keepOriginal: true);
        Assert.Equal(6, ta.GetProbes(Intensity.Quick).Count);                           // 3 seeds + 3 transformed
    }

    [Fact]
    public void Chain_Composes_OneProbePerSeed_WithChainProvenance()
    {
        var ta = new TransformedAttack(new FakeAttack(), [new Base64Transformer(), new Rot13Transformer()], TransformMode.Chain);
        var probes = ta.GetProbes(Intensity.Quick);

        Assert.Equal(3, probes.Count);
        Assert.All(probes, p => Assert.Equal("base64>rot13", p.Metadata![TransformProvenance.ChainKey]));
        Assert.All(probes, p => Assert.EndsWith("+base64+rot13", p.Id, StringComparison.Ordinal));
    }

    [Fact]
    public void Delegates_Identity_And_Evaluator_ToInner()
    {
        var inner = new FakeAttack();
        var ta = new TransformedAttack(inner, [new Base64Transformer()], TransformMode.Expand);

        Assert.Equal(inner.Name, ta.Name);
        Assert.Equal(inner.OwaspLlmId, ta.OwaspLlmId);
        Assert.Equal(inner.MitreAtlasIds, ta.MitreAtlasIds);
        Assert.Equal(inner.DefaultSeverity, ta.DefaultSeverity);
        Assert.IsType<ContainsTokenEvaluator>(ta.GetEvaluator());
    }

    [Fact]
    public void ChainDifficultyAccumulatesPerStep()
    {
        // Seed is Easy; each Raise transformer bumps once → Easy -> Moderate -> Hard (saturates), by design:
        // every additional decode layer makes the composed probe strictly harder. Expand bumps each sibling once.
        var seed = new FakeAttack().GetProbes(Intensity.Quick)[0];
        Assert.Equal(Difficulty.Easy, seed.Difficulty);

        var oneStep = new TransformedAttack(new FakeAttack(), [new Base64Transformer()], TransformMode.Chain)
            .GetProbes(Intensity.Quick);
        Assert.All(oneStep, p => Assert.Equal(Difficulty.Moderate, p.Difficulty));

        var twoStep = new TransformedAttack(new FakeAttack(), [new Base64Transformer(), new Rot13Transformer()], TransformMode.Chain)
            .GetProbes(Intensity.Quick);
        Assert.All(twoStep, p => Assert.Equal(Difficulty.Hard, p.Difficulty));

        // Expand: two siblings, each bumped exactly once off the seed → both Moderate, not Hard.
        var expand = new TransformedAttack(new FakeAttack(), [new Base64Transformer(), new Rot13Transformer()], TransformMode.Expand)
            .GetProbes(Intensity.Quick);
        Assert.All(expand, p => Assert.Equal(Difficulty.Moderate, p.Difficulty));
    }

    [Fact]
    public void EmptyTransformers_Throws() =>
        Assert.Throws<ArgumentException>(() => new TransformedAttack(new FakeAttack(), [], TransformMode.Expand));

    private sealed class BadNameTransformer : IProbeTransformer
    {
        public string Name => "bad>name";   // '>' is a reserved chain-provenance delimiter
        public DifficultyDelta DifficultyImpact => DifficultyDelta.Raise;
        public IEnumerable<AttackProbe> Transform(AttackProbe probe) => [probe];
    }

    [Fact]
    public void ReservedDelimiterInTransformerName_Throws() =>
        Assert.Throws<ArgumentException>(() => new TransformedAttack(new FakeAttack(), [new BadNameTransformer()], TransformMode.Expand));

    // === H3: reject capability attacks that TransformedAttack would silently downgrade to single-turn ===

    private class StubAttack : IAttackType
    {
        public string Name => "Stub";
        public string DisplayName => "Stub";
        public string Description => "stub";
        public string OwaspLlmId => "LLM01";
        public string[] MitreAtlasIds => [];
        public Severity DefaultSeverity => Severity.Medium;
        public IProbeEvaluator GetEvaluator() => new ContainsTokenEvaluator("X");
        public IReadOnlyList<AttackProbe> GetProbes(Intensity intensity) => [];
    }

    private sealed class FakeMultiTurnAttack : StubAttack, IMultiTurnAttack
    {
        public int MaxTurns => 3;
        public Task<string?> NextTurnAsync(MultiTurnContext context, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class FakeToolAwareAttack : StubAttack, IToolAwareAttack
    {
        public IReadOnlyList<CanaryTool> GetCanaryTools(Intensity intensity) => [];
    }

    private sealed class FakeTreeAttack : StubAttack, ITreeAttack
    {
        public int MaxDepth => 2;
        public int Branching => 2;
        public int Beam => 1;
        public int MaxNodes => 4;
        public string Objective(AttackProbe seed) => "obj";
    }

    [Fact]
    public void TransformingMultiTurnAttack_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => new TransformedAttack(new FakeMultiTurnAttack(), [new Base64Transformer()], TransformMode.Expand));
        Assert.Equal("inner", ex.ParamName);
    }

    [Fact]
    public void TransformingToolAwareAttack_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => new TransformedAttack(new FakeToolAwareAttack(), [new Base64Transformer()], TransformMode.Expand));
        Assert.Equal("inner", ex.ParamName);
    }

    [Fact]
    public void TransformingTreeAttack_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => new TransformedAttack(new FakeTreeAttack(), [new Base64Transformer()], TransformMode.Expand));
        Assert.Equal("inner", ex.ParamName);
    }

    // === L13: reject duplicate transformer Names (transformed probe Ids would collide) ===

    [Fact]
    public void DuplicateTransformerName_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => new TransformedAttack(new FakeAttack(), [new Base64Transformer(), new Base64Transformer()], TransformMode.Expand));
        Assert.Equal("transformers", ex.ParamName);
    }

    [Fact]
    public void SameTransformerInstanceTwice_Throws()
    {
        var b = new Base64Transformer();
        Assert.Throws<ArgumentException>(() => new TransformedAttack(new FakeAttack(), [b, b], TransformMode.Expand));
    }

    [Fact] // Jun14-M5: a recursive same-codec CHAIN (base64-of-base64) composes to distinct Ids and is a legitimate
    // evasion technique — it must NOT be rejected (the L13 dedup is Expand-only).
    public void Chain_DuplicateTransformerName_IsAllowed_AndComposesDistinctIds()
    {
        var ta = new TransformedAttack(new FakeAttack(), [new Base64Transformer(), new Base64Transformer()], TransformMode.Chain);

        var probes = ta.GetProbes(Intensity.Quick);

        Assert.NotEmpty(probes);
        Assert.All(probes, p => Assert.EndsWith("+base64+base64", p.Id));
        Assert.Equal(probes.Count, probes.Select(p => p.Id).Distinct().Count());
    }
}
