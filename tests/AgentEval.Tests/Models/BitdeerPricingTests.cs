// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests.Models;

/// <summary>
/// Bitdeer's models had no price in either table until 2026-10-21: a model comparison on Bitdeer reported cost as not
/// measured, and a GLM judge fell back to JudgeCostMap's gpt-4o-mini-grade default rate (that fallback is gone: an
/// unknown model is not priced). The rows hold the prices bitdeer.ai/en/pricing/ai-models charged that day.
/// </summary>
public sealed class BitdeerPricingTests
{
    [Theory]
    [InlineData("zai-org/GLM-5.3-Flash", 0.083, 0.275)]
    [InlineData("zai-org/GLM-5.3", 0.63, 1.98)]
    [InlineData("deepseek-ai/DeepSeek-V4-Flash", 0.14, 0.28)]
    [InlineData("deepseek-ai/DeepSeek-V4.1-Flash", 0.144, 0.576)]
    [InlineData("deepseek-ai/DeepSeek-V4.1-Flash-1017", 0.144, 0.576)]
    [InlineData("Qwen/Qwen3.8-27B", 0.40, 2.40)]
    [InlineData("nvidia/NVIDIA-Nemotron-3.5-Super-VL-120B-A12B", 0.10, 0.40)]
    public void EachBitdeerChatModel_IsPricedAtItsOwnRate_InBothTables(string model, double inputPerMillion, double outputPerMillion)
    {
        var pricing = ModelPricing.GetPricing(model);
        Assert.NotNull(pricing);
        Assert.Equal((decimal)inputPerMillion, pricing.Value.InputPricePerMillion);
        Assert.Equal((decimal)outputPerMillion, pricing.Value.OutputPricePerMillion);

        var judge = JudgeCostMap.GetRate(model);
        Assert.NotNull(judge);
        Assert.Equal(inputPerMillion / 1000, judge.Value.InputRatePer1K, 12);
        Assert.Equal(outputPerMillion / 1000, judge.Value.OutputRatePer1K, 12);
    }

    [Fact]
    public void GlmFlash_IsNotPricedAsGlm53_TheLongerKeyWins()
    {
        // "zai-org/GLM-5.3-Flash" contains "zai-org/GLM-5.3"; the substring rule must not price the cheaper model at
        // the bigger model's rate (7.6x on input).
        Assert.Equal(0.083m, ModelPricing.GetPricing("zai-org/GLM-5.3-Flash")!.Value.InputPricePerMillion);
        Assert.Equal(0.63m, ModelPricing.GetPricing("zai-org/GLM-5.3")!.Value.InputPricePerMillion);
    }

    [Fact]
    public void AGlmJudge_IsPricedAtItsOwnRate()
    {
        Assert.True(JudgeCostMap.IsRegistered("zai-org/GLM-5.3-Flash"));
        Assert.Equal(0.000083, JudgeCostMap.GetRate("zai-org/GLM-5.3-Flash")!.Value.InputRatePer1K, 12);
    }

    [Theory]
    [InlineData("BAAI/bge-m3")]
    [InlineData("BAAI/bge-reranker-v2-m3")]
    public void BitdeerEmbeddingAndRerankModels_ChargeInputOnly(string model)
    {
        var pricing = ModelPricing.GetPricing(model);
        Assert.NotNull(pricing);
        Assert.Equal(0.01m, pricing.Value.InputPricePerMillion);
        Assert.Equal(0m, pricing.Value.OutputPricePerMillion);
    }
}
