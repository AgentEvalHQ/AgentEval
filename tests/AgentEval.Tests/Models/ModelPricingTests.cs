// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Xunit;
using AgentEval.Models;

namespace AgentEval.Tests;

/// <summary>
/// Unit tests for ModelPricing cost estimation
/// </summary>
public class ModelPricingTests
{
    [Theory]
    [InlineData("gpt-4o", 1000, 500, 0.0125)] // 0.005 + 0.0075
    [InlineData("gpt-4o-mini", 1000, 500, 0.00045)] // 0.00015 + 0.0003
    [InlineData("gpt-3.5-turbo", 1000, 1000, 0.002)] // 0.0005 + 0.0015
    public void EstimateCost_WithKnownModel_ReturnsCorrectCost(
        string model, int input, int output, decimal expectedCost)
    {
        var cost = ModelPricing.EstimateCost(model, input, output);
        
        Assert.NotNull(cost);
        Assert.Equal(expectedCost, cost.Value, precision: 5);
    }
    
    [Theory]
    // BUG-11: a deployment name embedding a long, specific key must resolve to that key —
    // never to a shorter overlapping substring (gpt-4 ⊂ gpt-4o ⊂ gpt-4o-mini) depending on
    // ConcurrentDictionary enumeration order. gpt-4o-mini is ~200x cheaper than gpt-4.
    [InlineData("my-gpt-4o-mini-deployment")]
    [InlineData("prod_gpt-4o-mini_v2")]
    [InlineData("gpt-4o-mini")]
    public void GetPricing_OverlappingKeys_PrefersLongestMatch_Deterministically(string deployment)
    {
        var pricing = ModelPricing.GetPricing(deployment);

        Assert.NotNull(pricing);
        // gpt-4o-mini input = 0.00015/1K, NOT gpt-4 (0.03) or gpt-4o (0.005).
        Assert.Equal(0.00015m, pricing!.Value.InputPer1K);
        Assert.Equal(0.0006m, pricing.Value.OutputPer1K);
    }

    [Fact]
    public void GetPricing_DeploymentEmbeddingGpt4o_ResolvesToGpt4o_NotGpt4()
    {
        var pricing = ModelPricing.GetPricing("eastus-gpt-4o-chat");

        Assert.NotNull(pricing);
        Assert.Equal(0.005m, pricing!.Value.InputPer1K); // gpt-4o, not gpt-4 (0.03)
    }

    [Fact]
    public void EstimateCost_WithUnknownModel_ReturnsNull()
    {
        var cost = ModelPricing.EstimateCost("unknown-model-xyz", 1000, 500);
        
        Assert.Null(cost);
    }
    
    [Fact]
    public void EstimateCost_WithNullModel_ReturnsNull()
    {
        var cost = ModelPricing.EstimateCost(null, 1000, 500);
        
        Assert.Null(cost);
    }
    
    [Fact]
    public void EstimateCost_WithPartialModelName_FindsMatch()
    {
        // Should match "gpt-4o" even when using a deployment name like "my-gpt-4o-deployment"
        var cost = ModelPricing.EstimateCost("my-gpt-4o-deployment", 1000, 500);
        
        Assert.NotNull(cost);
    }
    
    [Fact]
    public void EstimateCost_IsCaseInsensitive()
    {
        var cost1 = ModelPricing.EstimateCost("GPT-4O", 1000, 500);
        var cost2 = ModelPricing.EstimateCost("gpt-4o", 1000, 500);
        
        Assert.NotNull(cost1);
        Assert.NotNull(cost2);
        Assert.Equal(cost1, cost2);
    }
    
    [Fact]
    public void SetPricing_AddsNewModel()
    {
        ModelPricing.SetPricing("custom-model", 0.01m, 0.02m);
        
        var cost = ModelPricing.EstimateCost("custom-model", 1000, 1000);
        
        Assert.NotNull(cost);
        Assert.Equal(0.03m, cost.Value); // 0.01 + 0.02
    }
    
    [Fact]
    public void EstimateCost_WithZeroTokens_ReturnsZero()
    {
        var cost = ModelPricing.EstimateCost("gpt-4o", 0, 0);
        
        Assert.NotNull(cost);
        Assert.Equal(0m, cost.Value);
    }
}
