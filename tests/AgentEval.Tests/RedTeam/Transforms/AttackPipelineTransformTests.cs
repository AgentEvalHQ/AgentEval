// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.RedTeam;
using AgentEval.RedTeam.Transforms;

namespace AgentEval.Tests.RedTeam.Transforms;

public class AttackPipelineTransformTests
{
    [Fact]
    public void WithTransform_AmplifiesLastAttack_OneTransformer_SameCount()
    {
        var seeds = AttackPipeline.Create().WithAttack(Attack.PromptInjection)
            .WithIntensity(Intensity.Quick).GetProbePreview();

        var transformed = AttackPipeline.Create().WithAttack(Attack.PromptInjection)
            .WithTransform(new Base64Transformer())
            .WithIntensity(Intensity.Quick).GetProbePreview();

        Assert.Equal(seeds.Count, transformed.Count);                                   // 1 transformer, no keepOriginal
        Assert.NotEmpty(transformed);
        Assert.All(transformed, p => Assert.DoesNotContain("PWNED", p.Prompt, StringComparison.Ordinal));
        Assert.All(transformed, p => Assert.Equal("base64", p.Metadata![TransformProvenance.ChainKey]));
    }

    [Fact]
    public void WithTransform_MultipleTransformers_FansOut()
    {
        var seeds = AttackPipeline.Create().WithAttack(Attack.PromptInjection)
            .WithIntensity(Intensity.Quick).GetProbePreview();

        var transformed = AttackPipeline.Create().WithAttack(Attack.PromptInjection)
            .WithTransform(new Base64Transformer(), new HexTransformer(), new Rot13Transformer())
            .WithIntensity(Intensity.Quick).GetProbePreview();

        Assert.Equal(seeds.Count * 3, transformed.Count);
    }

    [Fact]
    public void WithChainedTransform_Composes()
    {
        var transformed = AttackPipeline.Create().WithAttack(Attack.PromptInjection)
            .WithChainedTransform(new Base64Transformer(), new Rot13Transformer())
            .WithIntensity(Intensity.Quick).GetProbePreview();

        Assert.NotEmpty(transformed);
        Assert.All(transformed, p => Assert.Equal("base64>rot13", p.Metadata![TransformProvenance.ChainKey]));
    }

    [Fact]
    public void WithChainedTransform_KeepOriginal_AlsoEmitsSeeds()
    {
        var seeds = AttackPipeline.Create().WithAttack(Attack.PromptInjection)
            .WithIntensity(Intensity.Quick).GetProbePreview();

        var transformed = AttackPipeline.Create().WithAttack(Attack.PromptInjection)
            .WithChainedTransform(keepOriginal: true, new Base64Transformer(), new Rot13Transformer())
            .WithIntensity(Intensity.Quick).GetProbePreview();

        Assert.Equal(seeds.Count * 2, transformed.Count);                                // seed + one composed sibling each
        Assert.All(seeds, s => Assert.Contains(transformed, p => p.Id == s.Id));         // plaintext control retained
    }

    [Fact]
    public void WithTransform_BeforeAnyAttack_Throws() =>
        Assert.Throws<InvalidOperationException>(() => AttackPipeline.Create().WithTransform(new Base64Transformer()));

    [Fact]
    public void WithTransform_OnlyWrapsTheLastAttack()
    {
        // Jailbreak added first (untouched), PromptInjection added second + transformed.
        var pipeline = AttackPipeline.Create()
            .WithAttack(Attack.Jailbreak)
            .WithAttack(Attack.PromptInjection).WithTransform(new Base64Transformer())
            .WithIntensity(Intensity.Quick);

        var jbSeeds = AttackPipeline.Create().WithAttack(Attack.Jailbreak).WithIntensity(Intensity.Quick).GetProbePreview();
        var preview = pipeline.GetProbePreview();

        // Jailbreak probes appear verbatim (not base64-wrapped); PromptInjection probes are transformed.
        Assert.All(jbSeeds, jb => Assert.Contains(preview, p => p.Id == jb.Id));
    }
}
