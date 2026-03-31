using AgentEval.Memory.Models;
using AgentEval.Memory.Scenarios;
using Xunit;

namespace AgentEval.Memory.Tests.Scenarios;

public class ChattyConversationScenariosTests
{
    private readonly ChattyConversationScenarios _sut = new();

    [Fact]
    public void CreateBuriedFactsScenario_ReturnsScenarioWithFactsAndNoise()
    {
        var facts = new[] { MemoryFact.Create("My name is Alice") };

        var scenario = _sut.CreateBuriedFactsScenario(facts, 3.0);

        Assert.NotNull(scenario);
        Assert.Contains("Buried Facts", scenario.Name);
        Assert.True(scenario.Steps.Count > facts.Length, "Should have noise steps in addition to facts");
        Assert.NotEmpty(scenario.Queries);
    }

    [Fact]
    public void CreateTopicSwitchingScenario_ReturnsScenarioWithTopicChanges()
    {
        var facts = new[] { MemoryFact.Create("I work at Contoso") };

        var scenario = _sut.CreateTopicSwitchingScenario(facts, 5);

        Assert.NotNull(scenario);
        Assert.Contains("Topic", scenario.Name);
        Assert.NotEmpty(scenario.Steps);
        Assert.NotEmpty(scenario.Queries);
    }

    [Fact]
    public void CreateEmotionalDistractorScenario_ReturnsScenarioWithDistractors()
    {
        var facts = new[] { MemoryFact.Create("My birthday is March 15") };

        var scenario = _sut.CreateEmotionalDistractorScenario(facts, 5);

        Assert.NotNull(scenario);
        Assert.Contains("Emotional", scenario.Name);
        Assert.True(scenario.Steps.Count > facts.Length);
    }

    [Fact]
    public void CreateFalseInformationScenario_ReturnsScenarioWithTrueAndFalseFacts()
    {
        var trueFacts = new[] { MemoryFact.Create("My favorite color is blue") };
        var falseFacts = new[] { MemoryFact.Create("My favorite color is red") };

        var scenario = _sut.CreateFalseInformationScenario(trueFacts, falseFacts);

        Assert.NotNull(scenario);
        Assert.Equal("False Information Filtering", scenario.Name);
        Assert.NotEmpty(scenario.Steps);
        Assert.NotEmpty(scenario.Queries);
        // Queries should have forbidden facts
        Assert.Contains(scenario.Queries, q => q.ForbiddenFacts.Count > 0);
    }

    [Fact]
    public void ImplementsIChattyConversationScenarios()
    {
        Assert.IsAssignableFrom<IChattyConversationScenarios>(_sut);
    }
}
