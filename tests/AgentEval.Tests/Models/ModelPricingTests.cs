// Copyright (c) 2025-2026 AgentEval Contributors
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
