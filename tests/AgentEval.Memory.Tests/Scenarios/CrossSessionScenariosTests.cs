using AgentEval.Memory.Models;
using AgentEval.Memory.Scenarios;
using Xunit;

namespace AgentEval.Memory.Tests.Scenarios;

public class CrossSessionScenariosTests
{
    private readonly CrossSessionScenarios _sut = new();

    [Fact]
    public void CreateCrossSessionMemoryTest_ReturnsScenario()
    {
        var facts = new[] { MemoryFact.Create("My name is Bob") };

        var scenario = _sut.CreateCrossSessionMemoryTest(facts, 3, 60);

        Assert.NotNull(scenario);
        Assert.Contains("Cross-Session", scenario.Name);
        Assert.Contains(scenario.Steps, s => s.Content.Contains("SESSION_RESET_POINT"));
        Assert.NotEmpty(scenario.Queries);
        Assert.True((bool)scenario.Metadata!["RequiresSessionReset"]);
    }

    [Fact]
    public void CreateRestartPersistenceTest_ReturnsScenarioWithRestarts()
    {
        var facts = new[] { MemoryFact.Create("Important data") };

        var scenario = _sut.CreateRestartPersistenceTest(facts, 2);

        Assert.NotNull(scenario);
        Assert.Contains("Restart", scenario.Name);
        Assert.Contains(scenario.Steps, s => s.Content.Contains("SESSION_RESET_POINT"));
    }

    [Fact]
    public void CreateIncrementalLearningTest_AccumulatesFactsAcrossSessions()
    {
        var session1 = new[] { MemoryFact.Create("Fact A") };
        var session2 = new[] { MemoryFact.Create("Fact B") };

        var scenario = _sut.CreateIncrementalLearningTest(new[] { session1, session2 });

        Assert.NotNull(scenario);
        Assert.Contains("Incremental", scenario.Name);
        // Should query for ALL facts from all sessions
        Assert.Contains(scenario.Queries, q => q.ExpectedFacts.Count == 2);
    }

    [Fact]
    public void CreateContextSwitchingTest_ReturnsContextualScenario()
    {
        var ctx1Facts = new[] { MemoryFact.Create("Work: Project Alpha") };
        var ctx2Facts = new[] { MemoryFact.Create("Personal: Piano lessons") };
        var names = new[] { "Work", "Personal" };

        var scenario = _sut.CreateContextSwitchingTest(new[] { ctx1Facts, ctx2Facts }, names);

        Assert.NotNull(scenario);
        Assert.Contains("Context Switching", scenario.Name);
        Assert.Equal(2, scenario.Queries.Count);
    }

    [Fact]
    public void ImplementsICrossSessionScenarios()
    {
        Assert.IsAssignableFrom<ICrossSessionScenarios>(_sut);
    }
}
