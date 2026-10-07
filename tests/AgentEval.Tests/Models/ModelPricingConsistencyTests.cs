// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests.Models;

/// <summary>
/// AgentEval keeps two price tables: <see cref="ModelPricing"/> (run cost) and <see cref="JudgeCostMap"/> (judge cost).
/// They disagreed for months — gpt-4o at the 2024 price in one, gpt-5-mini a placeholder in the other — so the same model
/// cost two different amounts depending on which report you read. A model both tables know must cost the same in both.
/// </summary>
public sealed class ModelPricingConsistencyTests
{
    [Fact]
    public void AModelBothTablesKnow_CostsTheSameInBoth()
    {
        var known = new HashSet<string>(ModelPricing.KnownModels, StringComparer.OrdinalIgnoreCase);
        var shared = JudgeCostMap.Rates.Where(r => known.Contains(r.Key)).ToList();

        Assert.True(shared.Count >= 10, $"Expected the tables to share the common models; they share {shared.Count}.");
        Assert.All(shared, r =>
        {
            var p = ModelPricing.GetPricing(r.Key)!.Value;
            Assert.True(Math.Abs((double)p.InputPer1K - r.Value.InputRatePer1K) < 1e-12
                        && Math.Abs((double)p.OutputPer1K - r.Value.OutputRatePer1K) < 1e-12,
                $"{r.Key}: ModelPricing {p.InputPer1K}/{p.OutputPer1K} per 1K vs JudgeCostMap {r.Value.InputRatePer1K}/{r.Value.OutputRatePer1K}.");
        });
    }

    [Fact]
    public void Gpt55_IsPricedAsItself_NotAsTheGpt5Substring()
    {
        Assert.Equal(0.005, JudgeCostMap.GetRate("gpt-5.5").InputRatePer1K);
        Assert.Equal(0.030, JudgeCostMap.GetRate("gpt-5.5-2026-08-01").OutputRatePer1K);
        Assert.Equal(0.00125, JudgeCostMap.GetRate("gpt-5").InputRatePer1K);
    }
}
