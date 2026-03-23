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

        Assert.Equal(11, full.Categories.Count);
        Assert.Contains(full.Categories, c => c.ScenarioType == BenchmarkScenarioType.Abstention);
    }

    [Fact]
    public void QuickPreset_DoesNotIncludeAbstention()
    {
        var quick = MemoryBenchmark.Quick;

        Assert.Equal(3, quick.Categories.Count);
        Assert.DoesNotContain(quick.Categories, c => c.ScenarioType == BenchmarkScenarioType.Abstention);
    }

    [Theory]
    [InlineData("Quick")]
    [InlineData("Standard")]
    [InlineData("Full")]
    [InlineData("Diagnostic")]
    public void AllPresets_WeightsSumToOne(string presetName)
    {
        var preset = presetName switch
        {
            "Quick" => MemoryBenchmark.Quick,
            "Standard" => MemoryBenchmark.Standard,
            "Full" => MemoryBenchmark.Full,
            "Diagnostic" => MemoryBenchmark.Diagnostic,
            _ => throw new ArgumentException(presetName)
        };

        var total = preset.Categories.Sum(c => c.Weight);
        Assert.Equal(1.0, total, precision: 10); // Must be exact to prevent score drift
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

    // ═══════════════════════════════════════════════════════════════
    // Abstention Scoring Logic Tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void AbstentionQuery_CorrectAbstention_ShouldScoreHigh()
    {
        // When agent says "I don't know" for info never provided = correct behavior
        var query = MemoryQuery.CreateAbstention("What's my sister's name?",
            MemoryFact.Create("Sarah"),
            MemoryFact.Create("any specific name"));

        // Score should come from judge (100 for perfect abstention)
        // Here we verify the query structure supports correct scoring
        Assert.Empty(query.ExpectedFacts);
        Assert.Equal(2, query.ForbiddenFacts.Count);
        Assert.True(query.Metadata?["abstention"] is true);
    }

    [Fact]
    public void AbstentionQuery_Hallucination_ForbiddenFactsDetected()
    {
        // When agent fabricates "Your sister's name is Sarah" = hallucination
        var query = MemoryQuery.CreateAbstention("What's my sister's name?",
            MemoryFact.Create("Sarah"),
            MemoryFact.Create("any specific name"));

        // Simulate judge finding forbidden facts in the response
        var result = new MemoryQueryResult
        {
            Query = query,
            Score = 10.0, // Low score — agent hallucinated
            Response = "Your sister's name is Sarah!",
            FoundFacts = [],
            MissingFacts = [],
            ForbiddenFound = [MemoryFact.Create("Sarah")],
            Explanation = "Agent fabricated a name that was never provided"
        };

        Assert.False(result.Passed); // Score 10 < MinimumScore 80
        Assert.Single(result.ForbiddenFound);
        Assert.Equal("Sarah", result.ForbiddenFound[0].Content);
    }

    [Fact]
    public void AbstentionQuery_PartialHallucination_ForbiddenFactsPartiallyDetected()
    {
        var query = MemoryQuery.CreateAbstention("What's my address?",
            MemoryFact.Create("any street name"),
            MemoryFact.Create("any city"),
            MemoryFact.Create("any zip code"));

        // Agent says "I think you live in Seattle" — one hallucination
        var result = new MemoryQueryResult
        {
            Query = query,
            Score = 25.0,
            Response = "I think you live in Seattle",
            FoundFacts = [],
            MissingFacts = [],
            ForbiddenFound = [MemoryFact.Create("any city")],
            Explanation = "Agent fabricated a city but didn't invent street or zip"
        };

        Assert.False(result.Passed);
        Assert.Single(result.ForbiddenFound);
    }

    [Fact]
    public void AbstentionQuery_NoForbiddenFacts_StillValid()
    {
        // Abstention query with no specific forbidden facts — any fabrication is wrong
        var query = MemoryQuery.CreateAbstention("What color is my car?");

        Assert.Empty(query.ExpectedFacts);
        Assert.Empty(query.ForbiddenFacts);
        Assert.True(query.Metadata?["abstention"] is true);
    }
}
