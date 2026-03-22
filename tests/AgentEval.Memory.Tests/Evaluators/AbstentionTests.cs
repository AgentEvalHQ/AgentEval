// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.Models;
using AgentEval.Memory.Reporting;
using static AgentEval.Memory.Models.MemoryBenchmarkResult;
using Xunit;

namespace AgentEval.Memory.Tests.Evaluators;

public class AbstentionTests
{
    [Fact]
    public void MemoryQuery_CreateAbstention_HasEmptyExpectedFacts()
    {
        var query = MemoryQuery.CreateAbstention("What's my sister's name?",
            MemoryFact.Create("any specific name"));

        Assert.Empty(query.ExpectedFacts);
        Assert.Single(query.ForbiddenFacts);
        Assert.True(query.Metadata?.ContainsKey("abstention"));
    }

    [Fact]
    public void MemoryQuery_CreateAbstention_MultipleForbiddenFacts()
    {
        var query = MemoryQuery.CreateAbstention("What's my address?",
            MemoryFact.Create("any street name"),
            MemoryFact.Create("any city"),
            MemoryFact.Create("any zip code"));

        Assert.Empty(query.ExpectedFacts);
        Assert.Equal(3, query.ForbiddenFacts.Count);
    }

    [Fact]
    public void StandardPreset_IncludesAbstention()
    {
        var standard = MemoryBenchmark.Standard;

        Assert.Equal(7, standard.Categories.Count);
        Assert.Contains(standard.Categories, c => c.ScenarioType == BenchmarkScenarioType.Abstention);

        var abstention = standard.Categories.First(c => c.ScenarioType == BenchmarkScenarioType.Abstention);
        Assert.Equal(0.12, abstention.Weight);
        Assert.Equal("Abstention", abstention.Name);
    }

    [Fact]
    public void FullPreset_IncludesAbstention()
    {
        var full = MemoryBenchmark.Full;

        Assert.Equal(9, full.Categories.Count);
        Assert.Contains(full.Categories, c => c.ScenarioType == BenchmarkScenarioType.Abstention);
    }

    [Fact]
    public void QuickPreset_DoesNotIncludeAbstention()
    {
        var quick = MemoryBenchmark.Quick;

        Assert.Equal(3, quick.Categories.Count);
        Assert.DoesNotContain(quick.Categories, c => c.ScenarioType == BenchmarkScenarioType.Abstention);
    }

    [Fact]
    public void StandardPreset_WeightsSumToOne()
    {
        var total = MemoryBenchmark.Standard.Categories.Sum(c => c.Weight);
        Assert.Equal(1.0, total, precision: 2);
    }

    [Fact]
    public void FullPreset_WeightsSumToOne()
    {
        var total = MemoryBenchmark.Full.Categories.Sum(c => c.Weight);
        Assert.Equal(1.0, total, precision: 2);
    }

    [Fact]
    public void PentagonConsolidator_WithAbstention_FoldsIntoOrganization()
    {
        var categories = new List<BenchmarkCategoryResult>
        {
            new() { CategoryName = "Multi-Topic", Score = 80, Weight = 0.13,
                     ScenarioType = BenchmarkScenarioType.MultiTopic, Duration = TimeSpan.FromSeconds(1) },
            new() { CategoryName = "Abstention", Score = 60, Weight = 0.12,
                     ScenarioType = BenchmarkScenarioType.Abstention, Duration = TimeSpan.FromSeconds(1) }
        };

        var result = PentagonConsolidator.Consolidate(categories);

        Assert.True(result.ContainsKey("Organization"));
        Assert.Equal(70, result["Organization"]); // avg(80, 60)
    }

    [Fact]
    public void PentagonConsolidator_WithoutAbstention_OrganizationEqualsMultiTopic()
    {
        var categories = new List<BenchmarkCategoryResult>
        {
            new() { CategoryName = "Multi-Topic", Score = 80, Weight = 0.13,
                     ScenarioType = BenchmarkScenarioType.MultiTopic, Duration = TimeSpan.FromSeconds(1) }
        };

        var result = PentagonConsolidator.Consolidate(categories);

        Assert.Equal(80, result["Organization"]); // Only MultiTopic, no Abstention
    }

    [Fact]
    public void PentagonConsolidator_OnlyAbstention_OrganizationEqualsAbstention()
    {
        var categories = new List<BenchmarkCategoryResult>
        {
            new() { CategoryName = "Abstention", Score = 55, Weight = 0.12,
                     ScenarioType = BenchmarkScenarioType.Abstention, Duration = TimeSpan.FromSeconds(1) }
        };

        var result = PentagonConsolidator.Consolidate(categories);

        Assert.Equal(55, result["Organization"]); // Only Abstention
    }

    [Fact]
    public void Recommendation_IncludesAbstention()
    {
        var result = new MemoryBenchmarkResult
        {
            BenchmarkName = "Standard",
            Duration = TimeSpan.FromSeconds(10),
            CategoryResults =
            [
                new BenchmarkCategoryResult
                {
                    CategoryName = "Abstention", Score = 30, Weight = 0.12,
                    ScenarioType = BenchmarkScenarioType.Abstention, Duration = TimeSpan.FromSeconds(1)
                }
            ]
        };

        Assert.Contains(result.Recommendations, r => r.Contains("hallucinat", StringComparison.OrdinalIgnoreCase));
    }
}
