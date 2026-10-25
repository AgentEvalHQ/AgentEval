// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Models;

/// <summary>
/// T07: a judge model the table has no rate for is not priced. <see cref="JudgeCostMap"/> used to fall back to a
/// gpt-4o-mini rate (0.00015 / 0.0006 per 1K) for any unknown, null or blank model, so an Ollama, Mistral or
/// deployment-named judge carried a price nobody had set, while <c>ModelPricing</c> returned null for the same miss.
/// </summary>
public sealed class JudgeCostMapTests
{
    [Theory]
    [InlineData("llama3.1:8b")]
    [InlineData("mistral-large-2411")]
    [InlineData("my-azure-deployment")]
    public void UnknownModel_IsNotPriced(string model)
    {
        Assert.Null(JudgeCostMap.GetRate(model));
        Assert.Null(JudgeCostMap.EstimateCost(model, 1000, 1000)); // was 0.00075, the gpt-4o-mini rate
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void NullOrBlankModel_IsNotPriced(string? model)
    {
        Assert.Null(JudgeCostMap.GetRate(model));
        Assert.Null(JudgeCostMap.EstimateCost(model, 1000, 1000));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("llama3.1:8b")]
    [InlineData("gpt-4o-mini")]
    public void NoTokensSpent_CostsZero_WhateverTheModel(string? model)
    {
        // Nothing spent is a measured zero, not "not priced".
        Assert.Equal(0.0, JudgeCostMap.EstimateCost(model, 0, 0));
    }

    [Fact]
    public void KnownModel_IsPricedAtItsRate()
    {
        var rate = JudgeCostMap.GetRate("gpt-4o-mini");

        Assert.NotNull(rate);
        Assert.Equal(0.00015, rate.Value.InputRatePer1K, 12);
        Assert.Equal(0.00060, rate.Value.OutputRatePer1K, 12);
        Assert.Equal(0.00075, JudgeCostMap.EstimateCost("gpt-4o-mini", 1000, 1000)!.Value, 12);
    }

    [Fact]
    public void DatedSuffix_IsPriced_ThoughIsRegisteredIsFalse()
    {
        // IsRegistered is exact-key only; whether a model is priced is GetRate(model) is not null.
        Assert.False(JudgeCostMap.IsRegistered("gpt-4o-mini-2024-07-18"));
        Assert.NotNull(JudgeCostMap.GetRate("gpt-4o-mini-2024-07-18"));
        Assert.Equal(0.00075, JudgeCostMap.EstimateCost("gpt-4o-mini-2024-07-18", 1000, 1000)!.Value, 12);
    }
}
