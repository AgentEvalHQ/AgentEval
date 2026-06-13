// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.RedTeam;
using AgentEval.RedTeam.Transforms;

namespace AgentEval.Tests.RedTeam.Transforms;

public class TransformProvenanceTests
{
    [Fact]
    public void SuffixId_AppendsTransformName() =>
        Assert.Equal("PI-001+base64", TransformProvenance.SuffixId("PI-001", "base64"));

    [Theory]
    [InlineData(Difficulty.Easy, DifficultyDelta.Raise, Difficulty.Moderate)]
    [InlineData(Difficulty.Moderate, DifficultyDelta.Raise, Difficulty.Hard)]
    [InlineData(Difficulty.Hard, DifficultyDelta.Raise, Difficulty.Hard)]    // clamped at Hard
    [InlineData(Difficulty.Easy, DifficultyDelta.Lower, Difficulty.Easy)]    // clamped at Easy
    [InlineData(Difficulty.Moderate, DifficultyDelta.Same, Difficulty.Moderate)]
    public void Bump_ClampsToValidRange(Difficulty d, DifficultyDelta delta, Difficulty expected) =>
        Assert.Equal(expected, TransformProvenance.Bump(d, delta));

    [Fact]
    public void PayloadOf_PrefersMetadataPayload_ElseFallsBackToPrompt()
    {
        var withPayload = new AttackProbe
        {
            Id = "x", Prompt = "THE-PROMPT", Difficulty = Difficulty.Easy,
            Metadata = new Dictionary<string, object> { [TransformProvenance.PayloadKey] = "THE-PAYLOAD" }
        };
        Assert.Equal("THE-PAYLOAD", TransformProvenance.PayloadOf(withPayload));

        var noPayload = new AttackProbe { Id = "x", Prompt = "THE-PROMPT", Difficulty = Difficulty.Easy };
        Assert.Equal("THE-PROMPT", TransformProvenance.PayloadOf(noPayload));
    }

    [Fact]
    public void Stamp_RecordsSeedId_AppendsChain_SetsPayload()
    {
        var seed = new AttackProbe { Id = "PI-001", Prompt = "p", Difficulty = Difficulty.Easy };

        var m1 = TransformProvenance.Stamp(seed, "base64", "ENC1");
        Assert.Equal("PI-001", m1[TransformProvenance.SeedIdKey]);
        Assert.Equal("base64", m1[TransformProvenance.ChainKey]);
        Assert.Equal("ENC1", m1[TransformProvenance.PayloadKey]);

        // Chaining a second transform keeps the ORIGINAL seedId and appends to the chain.
        var probe2 = seed with { Id = "PI-001+base64", Metadata = m1 };
        var m2 = TransformProvenance.Stamp(probe2, "rot13", "ENC2");
        Assert.Equal("PI-001", m2[TransformProvenance.SeedIdKey]);
        Assert.Equal("base64>rot13", m2[TransformProvenance.ChainKey]);
        Assert.Equal("ENC2", m2[TransformProvenance.PayloadKey]);
    }

    [Fact]
    public void Transform_IsDeterministic()
    {
        var seed = new AttackProbe { Id = "PI-001", Prompt = "PWNED", Difficulty = Difficulty.Easy, ExpectedTokens = ["PWNED"] };
        var a = new Base64Transformer().Transform(seed).Single();
        var b = new Base64Transformer().Transform(seed).Single();

        Assert.Equal(a.Id, b.Id);
        Assert.Equal(a.Prompt, b.Prompt);
        Assert.Equal(a.Difficulty, b.Difficulty);
        Assert.Equal(a.Metadata![TransformProvenance.PayloadKey], b.Metadata![TransformProvenance.PayloadKey]);
    }
}
