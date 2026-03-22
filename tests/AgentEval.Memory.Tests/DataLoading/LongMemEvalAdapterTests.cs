// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.DataLoading;
using AgentEval.Memory.Models;
using Xunit;

namespace AgentEval.Memory.Tests.DataLoading;

public class LongMemEvalAdapterTests
{
    [Fact]
    public void LoadSubset_Returns10Scenarios()
    {
        var scenarios = LongMemEvalAdapter.LoadSubset();
        Assert.Equal(10, scenarios.Count);
    }

    [Fact]
    public void LoadSubset_AllScenariosHaveQueries()
    {
        var scenarios = LongMemEvalAdapter.LoadSubset();
        Assert.All(scenarios, s =>
        {
            Assert.NotEmpty(s.Queries);
            Assert.False(string.IsNullOrWhiteSpace(s.Name));
        });
    }

    [Fact]
    public void LoadSubset_AllScenariosHaveSteps()
    {
        var scenarios = LongMemEvalAdapter.LoadSubset();
        Assert.All(scenarios, s => Assert.NotEmpty(s.Steps));
    }

    [Fact]
    public void LoadSubset_ContainsAbstentionQuestions()
    {
        var scenarios = LongMemEvalAdapter.LoadSubset();
        var abstentionScenarios = scenarios.Where(s =>
            s.Description?.Contains("abstention") == true).ToList();

        Assert.True(abstentionScenarios.Count >= 2, "Subset should contain at least 2 abstention questions");
    }

    [Fact]
    public void LoadSubset_ContainsMultipleQuestionTypes()
    {
        var scenarios = LongMemEvalAdapter.LoadSubset();
        var types = scenarios
            .Select(s => s.Metadata?["question_type"]?.ToString())
            .Where(t => t != null)
            .Distinct()
            .ToList();

        Assert.True(types.Count >= 4, $"Subset should cover at least 4 question types, got {types.Count}: {string.Join(", ", types)}");
    }

    [Fact]
    public void LoadSubset_ScenariosHaveSourceMetadata()
    {
        var scenarios = LongMemEvalAdapter.LoadSubset();
        Assert.All(scenarios, s =>
        {
            Assert.NotNull(s.Metadata);
            Assert.Equal("LongMemEval", s.Metadata["source"]);
        });
    }

    [Fact]
    public void MapQuestionType_MapsCorrectly()
    {
        Assert.Equal(BenchmarkScenarioType.BasicRetention,
            LongMemEvalAdapter.MapQuestionType("single-session-user"));
        Assert.Equal(BenchmarkScenarioType.CrossSession,
            LongMemEvalAdapter.MapQuestionType("multi-session"));
        Assert.Equal(BenchmarkScenarioType.TemporalReasoning,
            LongMemEvalAdapter.MapQuestionType("temporal-reasoning"));
        Assert.Equal(BenchmarkScenarioType.FactUpdateHandling,
            LongMemEvalAdapter.MapQuestionType("knowledge-update"));
        Assert.Equal(BenchmarkScenarioType.Abstention,
            LongMemEvalAdapter.MapQuestionType("abstention"));
    }

    [Fact]
    public void LoadSubset_AbstentionQueriesUseCreateAbstention()
    {
        var scenarios = LongMemEvalAdapter.LoadSubset();
        var abstentionScenarios = scenarios.Where(s =>
            s.Description?.Contains("abstention") == true).ToList();

        foreach (var scenario in abstentionScenarios)
        {
            var query = scenario.Queries[0];
            Assert.Empty(query.ExpectedFacts); // Abstention = no expected facts
            Assert.NotEmpty(query.ForbiddenFacts); // Has forbidden facts to catch hallucinations
        }
    }

    [Fact]
    public void LoadSubset_MultiSessionScenario_HasMultipleSessions()
    {
        var scenarios = LongMemEvalAdapter.LoadSubset();
        var multiSession = scenarios.FirstOrDefault(s =>
            s.Description?.Contains("multi-session") == true);

        Assert.NotNull(multiSession);
        // Multi-session scenarios should have more steps (from multiple sessions)
        Assert.True(multiSession.Steps.Count >= 6,
            $"Multi-session scenario should have steps from multiple sessions, got {multiSession.Steps.Count}");
    }
}
