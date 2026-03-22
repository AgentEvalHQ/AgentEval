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
    public void Load_All11Scenarios_DeserializeCorrectly(string scenarioName)
    {
        var scenario = ScenarioLoader.Load(scenarioName);

        Assert.NotNull(scenario);
        Assert.False(string.IsNullOrEmpty(scenario.Category));
        Assert.False(string.IsNullOrEmpty(scenario.Name));
        Assert.True(scenario.Presets.ContainsKey("quick"), $"Scenario '{scenarioName}' missing 'quick' preset");
    }

    [Theory]
    [InlineData("basic-retention", "quick", 7)]    // 7 facts in quick
    [InlineData("basic-retention", "standard", 10)] // 7 + 3 inherited
    [InlineData("basic-retention", "full", 12)]     // 7 + 3 + 2 inherited
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

        // Quick uses context-small, Standard overrides to context-medium
        Assert.Equal("context-small", quickResolved.ContextPressure?.Corpus);
        Assert.Equal("context-medium", standardResolved.ContextPressure?.Corpus);
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
    public void ListAvailable_Returns11Scenarios()
    {
        var available = ScenarioLoader.ListAvailable();
        Assert.True(available.Count >= 11, $"Expected at least 11 scenarios, got {available.Count}");
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
