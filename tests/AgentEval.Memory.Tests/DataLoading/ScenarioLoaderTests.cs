// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.DataLoading;
using Xunit;

namespace AgentEval.Memory.Tests.DataLoading;

public class ScenarioLoaderTests
{
    [Theory]
    [InlineData("basic-retention")]
    [InlineData("temporal-reasoning")]
    [InlineData("noise-resilience")]
    [InlineData("fact-update-handling")]
    [InlineData("multi-topic")]
    [InlineData("reach-back-depth")]
    [InlineData("cross-session")]
    [InlineData("reducer-fidelity")]
    [InlineData("abstention")]
    [InlineData("conflict-resolution")]
    [InlineData("multi-session-reasoning")]
    [InlineData("preference-extraction")]
    public void Load_All12Scenarios_DeserializeCorrectly(string scenarioName)
    {
        var scenario = ScenarioLoader.Load(scenarioName);

        Assert.NotNull(scenario);
        Assert.False(string.IsNullOrEmpty(scenario.Category));
        Assert.False(string.IsNullOrEmpty(scenario.Name));
        Assert.True(scenario.Presets.ContainsKey("quick"), $"Scenario '{scenarioName}' missing 'quick' preset");
    }

    [Theory]
    [InlineData("basic-retention", "quick", 7)]    // 7 facts in quick
    [InlineData("basic-retention", "standard", 33)] // 7 + 26 inherited (synthesis + competing + specificity facts)
    [InlineData("basic-retention", "full", 37)]     // 7 + 26 + 4 inherited
    [InlineData("abstention", "quick", 3)]          // 3 planted facts
    [InlineData("noise-resilience", "quick", 4)]    // 4 buried facts
    public void ResolvePreset_InheritanceMergesFacts(string scenarioName, string preset, int expectedFactCount)
    {
        var scenario = ScenarioLoader.Load(scenarioName);
        var resolved = ScenarioLoader.ResolvePreset(scenario, preset);

        Assert.Equal(expectedFactCount, resolved.Facts.Count);
    }

    [Fact]
    public void ResolvePreset_StandardExtendsQuick_HasBothQueries()
    {
        var scenario = ScenarioLoader.Load("basic-retention");
        var quickResolved = ScenarioLoader.ResolvePreset(scenario, "quick");
        var standardResolved = ScenarioLoader.ResolvePreset(scenario, "standard");

        Assert.True(standardResolved.Queries.Count > quickResolved.Queries.Count,
            "Standard should have more queries than Quick (inherited + additional)");
    }

    [Fact]
    public void ResolvePreset_ContextPressureOverride()
    {
        var scenario = ScenarioLoader.Load("basic-retention");

        var quickResolved = ScenarioLoader.ResolvePreset(scenario, "quick");
        var standardResolved = ScenarioLoader.ResolvePreset(scenario, "standard");

        // Quick uses context-small, Standard overrides to context-stress
        Assert.Equal("context-small", quickResolved.ContextPressure?.Corpus);
        Assert.Equal("context-stress", standardResolved.ContextPressure?.Corpus);
    }

    [Fact]
    public void ResolvePreset_UnknownPreset_FallsBackToQuick()
    {
        var scenario = ScenarioLoader.Load("basic-retention");
        var resolved = ScenarioLoader.ResolvePreset(scenario, "nonexistent");

        // Should fall back to quick preset
        Assert.True(resolved.Facts.Count > 0);
    }

    [Fact]
    public void ResolvePreset_AbstentionQueries_HaveAbstentionFlag()
    {
        var scenario = ScenarioLoader.Load("abstention");
        var resolved = ScenarioLoader.ResolvePreset(scenario, "quick");

        var abstentionQueries = resolved.Queries.Where(q => q.Abstention).ToList();
        Assert.True(abstentionQueries.Count >= 6, "Should have at least 6 abstention queries");

        var recallQueries = resolved.Queries.Where(q => !q.Abstention).ToList();
        Assert.True(recallQueries.Count >= 2, "Should have at least 2 recall queries");
    }

    [Theory]
    [InlineData("basic-retention")]
    [InlineData("temporal-reasoning")]
    [InlineData("noise-resilience")]
    [InlineData("multi-topic")]
    [InlineData("abstention")]
    [InlineData("fact-update-handling")]
    [InlineData("preference-extraction")]
    [InlineData("conflict-resolution")]
    public void ResolvePreset_StandardHasMoreFactsThanQuick(string scenarioName)
    {
        var scenario = ScenarioLoader.Load(scenarioName);
        var quick = ScenarioLoader.ResolvePreset(scenario, "quick");
        var standard = ScenarioLoader.ResolvePreset(scenario, "standard");

        Assert.True(standard.Facts.Count >= quick.Facts.Count,
            $"{scenarioName}: Standard ({standard.Facts.Count}) should have >= facts than Quick ({quick.Facts.Count})");
    }

    [Fact]
    public void FactUpdateHandling_QuickPreset_HasUpdateFacts()
    {
        var scenario = ScenarioLoader.Load("fact-update-handling");
        var resolved = ScenarioLoader.ResolvePreset(scenario, "quick");

        // Quick must have both original AND update facts so queries expecting updated values pass
        Assert.True(resolved.Facts.Count >= 4,
            $"fact-update-handling quick needs original + update facts, got {resolved.Facts.Count}");

        var hasColorUpdate = resolved.Facts.Any(f => f.Content.Contains("green", StringComparison.OrdinalIgnoreCase));
        Assert.True(hasColorUpdate, "Quick should contain the color update fact (blue → green)");
    }

    [Theory]
    [InlineData("basic-retention", "standard")]
    [InlineData("noise-resilience", "standard")]
    [InlineData("abstention", "standard")]
    [InlineData("fact-update-handling", "standard")]
    public void ResolvePreset_HardenedScenarios_HaveForbiddenFacts(string scenarioName, string preset)
    {
        var scenario = ScenarioLoader.Load(scenarioName);
        var resolved = ScenarioLoader.ResolvePreset(scenario, preset);

        var queriesWithForbidden = resolved.Queries.Where(q => q.ForbiddenFacts?.Count > 0).ToList();
        Assert.True(queriesWithForbidden.Count >= 1,
            $"{scenarioName}/{preset}: Hardened scenario should have at least 1 query with forbidden_facts, got {queriesWithForbidden.Count}");
    }

    [Theory]
    [InlineData("basic-retention", "standard", "synthesis")]
    [InlineData("abstention", "standard", "counterfactual")]
    [InlineData("temporal-reasoning", "standard", "temporal")]
    [InlineData("fact-update-handling", "standard", "update")]
    public void ResolvePreset_HardenedQueries_HaveQueryType(string scenarioName, string preset, string expectedQueryType)
    {
        var scenario = ScenarioLoader.Load(scenarioName);
        var resolved = ScenarioLoader.ResolvePreset(scenario, preset);

        var matchingQueries = resolved.Queries.Where(q => q.QueryType == expectedQueryType).ToList();
        Assert.True(matchingQueries.Count >= 1,
            $"{scenarioName}/{preset}: Should have at least 1 query with query_type='{expectedQueryType}', got {matchingQueries.Count}");
    }

    [Theory]
    [InlineData("basic-retention", "standard", "specificity_attack")]
    [InlineData("abstention", "standard", "specificity_attack")]
    [InlineData("fact-update-handling", "standard", "correction_chain")]
    [InlineData("preference-extraction", "standard", "preference")]
    public void ResolvePreset_AdditionalQueryTypes_Present(string scenarioName, string preset, string expectedQueryType)
    {
        var scenario = ScenarioLoader.Load(scenarioName);
        var resolved = ScenarioLoader.ResolvePreset(scenario, preset);

        var matching = resolved.Queries.Where(q => q.QueryType == expectedQueryType).ToList();
        Assert.True(matching.Count >= 1,
            $"{scenarioName}/{preset}: Should have at least 1 query with query_type='{expectedQueryType}', got {matching.Count}");
    }

    [Theory]
    [InlineData("multi-topic", "standard")]
    [InlineData("preference-extraction", "standard")]
    public void ResolvePreset_PhaseA_ForbiddenFactsAdded(string scenarioName, string preset)
    {
        var scenario = ScenarioLoader.Load(scenarioName);
        var resolved = ScenarioLoader.ResolvePreset(scenario, preset);

        var queriesWithForbidden = resolved.Queries.Where(q => q.ForbiddenFacts?.Count > 0).ToList();
        Assert.True(queriesWithForbidden.Count >= 1,
            $"{scenarioName}/{preset}: Should have forbidden_facts after Phase A hardening, got {queriesWithForbidden.Count}");
    }

    [Theory]
    [InlineData("basic-retention")]
    [InlineData("noise-resilience")]
    [InlineData("abstention")]
    [InlineData("preference-extraction")]
    public void ResolvePreset_StandardFacts_HaveTimestamps(string scenarioName)
    {
        var scenario = ScenarioLoader.Load(scenarioName);
        var resolved = ScenarioLoader.ResolvePreset(scenario, "standard");

        // At least half the standard facts should have timestamps after Phase A
        var factsWithTimestamp = resolved.Facts.Where(f => !string.IsNullOrEmpty(f.Timestamp)).ToList();
        Assert.True(factsWithTimestamp.Count >= resolved.Facts.Count / 2,
            $"{scenarioName}: Expected at least half of {resolved.Facts.Count} standard facts to have timestamps, got {factsWithTimestamp.Count}");
    }

    [Fact]
    public void Abstention_Standard_HasNegationFacts()
    {
        var scenario = ScenarioLoader.Load("abstention");
        var resolved = ScenarioLoader.ResolvePreset(scenario, "standard");

        var coffeeFact = resolved.Facts.Any(f => f.Content.Contains("coffee", StringComparison.OrdinalIgnoreCase));
        var carFact = resolved.Facts.Any(f => f.Content.Contains("sold my car", StringComparison.OrdinalIgnoreCase));
        var socialFact = resolved.Facts.Any(f => f.Content.Contains("social media", StringComparison.OrdinalIgnoreCase));

        Assert.True(coffeeFact, "Should have coffee negation fact");
        Assert.True(carFact, "Should have car/bike negation fact");
        Assert.True(socialFact, "Should have social media negation fact");
    }

    [Fact]
    public void Abstention_Standard_HasNegationTrapQueries()
    {
        var scenario = ScenarioLoader.Load("abstention");
        var resolved = ScenarioLoader.ResolvePreset(scenario, "standard");

        var negationQueries = resolved.Queries.Where(q =>
            q.Question.Contains("coffee", StringComparison.OrdinalIgnoreCase) ||
            q.Question.Contains("park", StringComparison.OrdinalIgnoreCase) ||
            q.Question.Contains("Instagram", StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.True(negationQueries.Count >= 3, $"Should have 3 negation trap queries, got {negationQueries.Count}");
    }

    [Theory]
    [InlineData("basic-retention", 0.05)]
    [InlineData("noise-resilience", 0.05)]
    [InlineData("temporal-reasoning", 0.05)]
    public void ResolvePreset_EarlyPositionBurial_HasFactsBelow005(string scenarioName, double maxPosition)
    {
        var scenario = ScenarioLoader.Load(scenarioName);
        var resolved = ScenarioLoader.ResolvePreset(scenario, "standard");

        var earlyFacts = resolved.Facts.Where(f => f.FractionalPosition.HasValue && f.FractionalPosition.Value <= maxPosition).ToList();
        Assert.True(earlyFacts.Count >= 1,
            $"{scenarioName}: Should have at least 1 fact at position <= {maxPosition} for early-burial, got {earlyFacts.Count}");
    }

    [Theory]
    [InlineData("basic-retention")]
    [InlineData("temporal-reasoning")]
    [InlineData("noise-resilience")]
    [InlineData("multi-topic")]
    [InlineData("abstention")]
    [InlineData("preference-extraction")]
    public void ResolvePreset_DistractorTurns_DeserializeCorrectly(string scenarioName)
    {
        var scenario = ScenarioLoader.Load(scenarioName);
        // Standard preset should have distractor_turns in context_pressure
        var standard = scenario.Presets["standard"];

        Assert.NotNull(standard.ContextPressure);
        Assert.NotNull(standard.ContextPressure.DistractorTurns);
        Assert.True(standard.ContextPressure.DistractorTurns.Count >= 4,
            $"{scenarioName}: Should have at least 4 distractor turns, got {standard.ContextPressure?.DistractorTurns?.Count ?? 0}");

        // Each distractor should have non-empty user and assistant
        foreach (var d in standard.ContextPressure!.DistractorTurns!)
        {
            Assert.False(string.IsNullOrEmpty(d.User), "Distractor user turn should not be empty");
            Assert.False(string.IsNullOrEmpty(d.Assistant), "Distractor assistant turn should not be empty");
        }
    }

    [Theory]
    [InlineData("noise-resilience", 18)]
    [InlineData("basic-retention", 10)]
    public void ResolvePreset_RedHerrings_IncreasedNoiseCount(string scenarioName, int minNoiseCount)
    {
        var scenario = ScenarioLoader.Load(scenarioName);
        var resolved = ScenarioLoader.ResolvePreset(scenario, "standard");

        Assert.True(resolved.NoiseBetweenFacts.Count >= minNoiseCount,
            $"{scenarioName}: Should have at least {minNoiseCount} noise entries after red herrings, got {resolved.NoiseBetweenFacts.Count}");
    }

    [Fact]
    public void LoadToTargetTokens_RepeatsCorpusToReachTarget()
    {
        // context-small is ~8K tokens. Requesting 20K should repeat it ~3x
        var turns = CorpusLoader.LoadToTargetTokens("context-small", 20000);

        // Should have more than the base 15 turns
        Assert.True(turns.Count > 15,
            $"LoadToTargetTokens(20000) should repeat context-small beyond 15 turns, got {turns.Count}");
    }

    [Fact]
    public void LoadToTargetTokens_SmallTarget_ReturnsAtLeastOneCopy()
    {
        var turns = CorpusLoader.LoadToTargetTokens("context-small", 100);

        // Even with tiny target, should return at least 1 copy
        Assert.True(turns.Count >= 15,
            $"Should return at least 1 full copy (15 turns), got {turns.Count}");
    }

    [Fact]
    public void LoadToTargetTokens_LargeTarget_ProducesLargeCorpus()
    {
        // 192K tokens target with context-stress (~120K) should produce ~2x
        var turns = CorpusLoader.LoadToTargetTokens("context-stress", 192000);

        Assert.True(turns.Count >= 400,
            $"192K target with context-stress should produce 400+ turns, got {turns.Count}");
    }

    [Fact]
    public void Load_NonExistent_ThrowsFileNotFoundException()
    {
        Assert.Throws<FileNotFoundException>(() => ScenarioLoader.Load("does-not-exist"));
    }

    [Fact]
    public void ConflictResolution_HasForbiddenFacts()
    {
        var scenario = ScenarioLoader.Load("conflict-resolution");
        var resolved = ScenarioLoader.ResolvePreset(scenario, "quick");

        var queriesWithForbidden = resolved.Queries.Where(q => q.ForbiddenFacts?.Count > 0).ToList();
        Assert.True(queriesWithForbidden.Count >= 2,
            "Conflict resolution should have queries with forbidden facts (outdated info)");
    }

    [Fact]
    public void MultiSessionReasoning_HasFacts()
    {
        var scenario = ScenarioLoader.Load("multi-session-reasoning");
        var resolved = ScenarioLoader.ResolvePreset(scenario, "quick");

        Assert.True(resolved.Facts.Count >= 2, "Multi-session reasoning needs at least 2 facts to split across sessions");
        Assert.True(resolved.Queries.Count >= 1, "Should have at least 1 cross-session query");
    }

    [Fact]
    public void ListAvailable_Returns12Scenarios()
    {
        var available = ScenarioLoader.ListAvailable();
        Assert.True(available.Count >= 12, $"Expected at least 12 scenarios, got {available.Count}");
    }

    [Theory]
    [InlineData("basic-retention")]
    [InlineData("temporal-reasoning")]
    [InlineData("noise-resilience")]
    [InlineData("multi-topic")]
    public void ResolvePreset_HasNoiseBetweenFacts(string scenarioName)
    {
        var scenario = ScenarioLoader.Load(scenarioName);
        var resolved = ScenarioLoader.ResolvePreset(scenario, "quick");

        Assert.True(resolved.NoiseBetweenFacts.Count > 0,
            $"Scenario '{scenarioName}' should have noise between facts");
    }
}
