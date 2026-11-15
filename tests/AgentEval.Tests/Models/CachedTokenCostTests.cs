// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Models;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Models;

public sealed class CachedTokenCostTests
{
    private static readonly TokenPricing Rates = new(0.001m, 0.002m, 0.0001m);

    [Theory]
    [InlineData(8000L, "0.0048", TokenCostStatus.CacheAdjusted)]
    [InlineData(10000L, "0.003", TokenCostStatus.CacheAdjusted)]
    [InlineData(0L, "0.012", TokenCostStatus.Standard)]
    [InlineData(null, "0.012", TokenCostStatus.UndiscountedUnknownCache)]
    public void Calculate_CacheSubset_ChargesEachTokenOnce(long? cached, string expected, TokenCostStatus status)
    {
        var result = TokenCostEstimate.Calculate(10000, 1000, cached, Rates);
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), result.Amount);
        Assert.Equal(status, result.Status);
    }

    [Fact]
    public void Calculate_CachedRateMissing_LabelsUndiscountedEstimate()
    {
        var result = TokenCostEstimate.Calculate(10000, 1000, 8000, new(0.001m, 0.002m));
        Assert.Equal(0.012m, result.Amount);
        Assert.Equal(TokenCostStatus.UndiscountedMissingCacheRate, result.Status);
    }

    [Fact]
    public void Calculate_FreeCache_DoesNotSubstituteOrdinaryRate() =>
        Assert.Equal(0m, TokenCostEstimate.Calculate(1000, 0, 1000, new(1, 1, 0)).Amount);

    [Theory]
    [InlineData(-1L, 0L, 0L)]
    [InlineData(0L, -1L, 0L)]
    [InlineData(100L, 10L, -1L)]
    [InlineData(100L, 10L, 101L)]
    public void Calculate_InvalidCounts_RefusesPrice(long input, long output, long cached)
    {
        var result = TokenCostEstimate.Calculate(input, output, cached, Rates);
        Assert.Null(result.Amount);
        Assert.Equal(TokenCostStatus.Invalid, result.Status);
    }

    [Theory]
    [InlineData(null, 5L)]
    [InlineData(5L, null)]
    [InlineData(null, null)]
    public void Calculate_MissingUsage_IsNotFree(long? input, long? output) =>
        Assert.Null(TokenCostEstimate.Calculate(input, output, null, Rates).Amount);

    [Fact]
    public void Calculate_ZeroUsageAndUnknownPrice_IsMeasuredZero() =>
        Assert.Equal(0m, TokenCostEstimate.Calculate(0, 0, 0, null).Amount);

    [Fact]
    public void Calculate_UnknownPriceWithUsage_RemainsUnpriced() =>
        Assert.Null(TokenCostEstimate.Calculate(1, 0, 0, null).Amount);

    [Fact]
    public void Calculate_OverflowAndNegativeRates_AreInvalid()
    {
        Assert.Equal(TokenCostStatus.Invalid, TokenCostEstimate.Calculate(long.MaxValue, 0, 0, new(decimal.MaxValue, 0)).Status);
        Assert.Equal(TokenCostStatus.Invalid, TokenCostEstimate.Calculate(1, 1, 0, new(-1, 0)).Status);
        Assert.Equal(TokenCostStatus.Invalid, TokenCostEstimate.Calculate(1, 1, 0, new(0, -1)).Status);
        Assert.Equal(TokenCostStatus.Invalid, TokenCostEstimate.Calculate(1, 1, 0, new(0, 0, -1)).Status);
    }

    [Fact]
    public void Snapshot_RegistrationChanges_DoesNotChangeCapturedCacheRates()
    {
        var key = "snapshot-" + Guid.NewGuid();
        ModelPricing.SetPricing(key, Rates with { Source = "customer-v1" });
        var snapshot = ModelPricing.CaptureSnapshot();
        ModelPricing.SetPricing(key, 2, 3);
        Assert.Equal(0.0001m, snapshot.GetTokenPricing(key)!.CachedInputPer1K);
        Assert.Equal("customer-v1", snapshot.GetTokenPricing(key)!.Source);
        Assert.Equal((0.001m, 0.002m), snapshot.Rates[key]);
        Assert.Null(ModelPricing.GetTokenPricing(key)!.CachedInputPer1K);
        Assert.Null(snapshot.GetTokenPricing("unknown"));
    }

    [Theory]
    [InlineData("gpt-4o")]
    [InlineData("gpt-4o-mini")]
    [InlineData("gpt-4.1")]
    public void BundledCacheRates_AgentAndJudgeTables_Agree(string model)
    {
        var agent = ModelPricing.GetTokenPricing(model)!;
        var judge = JudgeCostMap.EstimateCostDetailed(model, 10000, 1000, 8000);
        Assert.Equal(TokenCostEstimate.Calculate(10000, 1000, 8000, agent).Amount, judge.Amount);
        Assert.Equal(agent.Source, judge.Pricing!.Source);
        Assert.Contains("2026-11-11", agent.Source!);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.MaxValue)]
    public void JudgeRate_NonDecimalNumber_IsUnpriced(double invalid)
    {
        var key = "invalid-" + Guid.NewGuid();
        JudgeCostMap.Register(key, new(invalid, 1));
        Assert.Null(JudgeCostMap.EstimateCost(key, 10, 10));
    }

    [Fact]
    public void Mapping_LargeCounts_PreservesLosslessUsageForPricing()
    {
        var usage = TokenUsageMapping.FromUsage(new UsageDetails
        {
            InputTokenCount = (long)int.MaxValue + 100, OutputTokenCount = 10, CachedInputTokenCount = int.MaxValue
        })!;
        Assert.Equal(int.MaxValue, usage.PromptTokens);
        Assert.Equal((long)int.MaxValue + 100, usage.ReportedInputTokenCount);
        Assert.Equal(10L, usage.ReportedOutputTokenCount);
        Assert.Equal(int.MaxValue, usage.CachedInputTokenCount);
    }

    [Theory]
    [InlineData(5L, 7L, 12L)]
    [InlineData(null, 7L, null)]
    [InlineData(5L, null, null)]
    [InlineData(0L, 0L, 0L)]
    [InlineData(11L, 0L, -1L)]
    public void StreamingAccumulator_MultipleInputFrames_PreservesCacheCompleteness(long? first, long? second, long? expected)
    {
        var accumulator = new StreamingUsageAccumulator();
        accumulator.Add(new() { InputTokenCount = 10, CachedInputTokenCount = first });
        accumulator.Add(new() { InputTokenCount = 10, CachedInputTokenCount = second });
        accumulator.Add(new() { OutputTokenCount = 3 });
        var usage = accumulator.ToTokenUsage()!;
        Assert.Equal(expected, usage.CachedInputTokenCount);
        Assert.Equal(20, usage.PromptTokens);
    }

    [Fact]
    public void StreamingAccumulator_CacheOnlyFrame_AddsOnceAndPreservesUnknownEarlierInput()
    {
        var known = new StreamingUsageAccumulator();
        known.Add(new() { InputTokenCount = 10, OutputTokenCount = 1, CachedInputTokenCount = 2 });
        known.Add(new() { CachedInputTokenCount = 3 });
        Assert.Equal(5, known.ToTokenUsage()!.CachedInputTokenCount);
        var unknown = new StreamingUsageAccumulator();
        unknown.Add(new() { InputTokenCount = 10, OutputTokenCount = 1 });
        unknown.Add(new() { CachedInputTokenCount = 3 });
        Assert.Null(unknown.ToTokenUsage()!.CachedInputTokenCount);
    }
}
